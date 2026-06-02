// mgsv_modmgr core implementation.
#include "core.h"

#include <algorithm>
#include <cstdint>
#include <cstring>
#include <fstream>
#include <sstream>
#include <stdexcept>

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

namespace core {

State    g_state;
fs::path g_exe_dir;
fs::path g_state_path;
fs::path g_baseline_dir;
fs::path g_mods_dir;
fs::path g_tmp_dir;

static LogFn g_log_fn = [](const std::string& s){ /* default sink: drop */ (void)s; };

void set_logger(LogFn fn) { g_log_fn = std::move(fn); }
void log(const std::string& s) { g_log_fn(s); }

static std::string exe_dir_string() {
    char buf[MAX_PATH]; GetModuleFileNameA(nullptr, buf, MAX_PATH);
    return fs::path(buf).parent_path().string();
}

void init_paths() {
    g_exe_dir     = exe_dir_string();
    g_state_path  = g_exe_dir / "state.txt";
    g_baseline_dir = g_exe_dir / "root";
    g_mods_dir    = g_exe_dir / "mods";
    g_tmp_dir     = g_exe_dir / "tmp";
}

static fs::path path_dict_file()     { return g_state.game_root / "PathDictionary.txt"; }
static fs::path explicit_dict_file() { return g_state.game_root / "ExplicitPathDictionary.txt"; }

// Forward decls for items defined further down but referenced by dictionary code above them.
static fs::path mod_unpack_dir(const std::string& id);
static void     parse_metadata(const fs::path& meta_path, ModInfo& m);

// ---------- string helpers ----------

static std::string trim(std::string s) {
    auto sp = [](char c){ return c==' '||c=='\t'||c=='\r'||c=='\n'; };
    while (!s.empty() && sp(s.back())) s.pop_back();
    size_t i = 0; while (i < s.size() && sp(s[i])) ++i;
    return s.substr(i);
}

static bool starts_with(const std::string& s, const std::string& p) {
    return s.size() >= p.size() && std::memcmp(s.data(), p.data(), p.size()) == 0;
}

static bool ends_with(const std::string& s, const std::string& p) {
    return s.size() >= p.size() && std::memcmp(s.data()+s.size()-p.size(), p.data(), p.size()) == 0;
}

static std::string to_lower(std::string s) {
    for (auto& c : s) c = (char)std::tolower((unsigned char)c);
    return s;
}

static std::vector<std::string> split(const std::string& s, char d) {
    std::vector<std::string> r; std::string cur;
    for (char c : s) { if (c==d) { r.push_back(cur); cur.clear(); } else cur.push_back(c); }
    r.push_back(cur); return r;
}

[[noreturn]] static void die(const std::string& msg) { throw std::runtime_error(msg); }

static std::string quote(const fs::path& p) { return "\"" + p.string() + "\""; }

// ---------- process runner with captured output ----------

// Runs a command via cmd.exe, streaming combined stdout/stderr through core::log.
// Returns the child's exit code.
static int run_process(const std::string& cmdline) {
    log("$ " + cmdline);

    SECURITY_ATTRIBUTES sa{}; sa.nLength = sizeof(sa); sa.bInheritHandle = TRUE;
    HANDLE rd = nullptr, wr = nullptr;
    if (!CreatePipe(&rd, &wr, &sa, 0)) die("CreatePipe failed");
    SetHandleInformation(rd, HANDLE_FLAG_INHERIT, 0);

    STARTUPINFOA si{}; si.cb = sizeof(si);
    si.dwFlags    = STARTF_USESTDHANDLES | STARTF_USESHOWWINDOW;
    si.wShowWindow = SW_HIDE;
    si.hStdOutput = wr; si.hStdError = wr; si.hStdInput = GetStdHandle(STD_INPUT_HANDLE);

    PROCESS_INFORMATION pi{};
    // Skip cmd.exe — its quote-stripping mangles paths with embedded quotes.
    // CreateProcess parses argv[0] from the command line directly.
    std::vector<char> mut(cmdline.begin(), cmdline.end()); mut.push_back(0);

    BOOL ok = CreateProcessA(nullptr, mut.data(), nullptr, nullptr, TRUE,
                             CREATE_NO_WINDOW, nullptr, nullptr, &si, &pi);
    CloseHandle(wr);
    if (!ok) { CloseHandle(rd); die("CreateProcess failed for: " + cmdline); }

    std::string carry;
    char buf[4096];
    DWORD got = 0;
    while (ReadFile(rd, buf, sizeof(buf), &got, nullptr) && got > 0) {
        carry.append(buf, buf + got);
        size_t pos;
        while ((pos = carry.find('\n')) != std::string::npos) {
            std::string line = carry.substr(0, pos);
            if (!line.empty() && line.back() == '\r') line.pop_back();
            log(line);
            carry.erase(0, pos + 1);
        }
    }
    if (!carry.empty()) log(carry);

    WaitForSingleObject(pi.hProcess, INFINITE);
    DWORD code = 0; GetExitCodeProcess(pi.hProcess, &code);
    CloseHandle(pi.hProcess); CloseHandle(pi.hThread); CloseHandle(rd);
    return (int)code;
}

// ---------- tiny XML attr extractor ----------

static std::string xml_attr(const std::string& tag_text, const std::string& name) {
    size_t pos = 0;
    while ((pos = tag_text.find(name, pos)) != std::string::npos) {
        if (pos == 0 || !(tag_text[pos-1]==' '||tag_text[pos-1]=='\t'||tag_text[pos-1]=='\n')) { ++pos; continue; }
        size_t eq = pos + name.size();
        while (eq < tag_text.size() && (tag_text[eq]==' '||tag_text[eq]=='\t')) ++eq;
        if (eq >= tag_text.size() || tag_text[eq] != '=') { ++pos; continue; }
        ++eq;
        while (eq < tag_text.size() && (tag_text[eq]==' '||tag_text[eq]=='\t')) ++eq;
        if (eq >= tag_text.size() || (tag_text[eq] != '"' && tag_text[eq] != '\'')) { ++pos; continue; }
        char q = tag_text[eq]; ++eq;
        size_t end = tag_text.find(q, eq);
        if (end == std::string::npos) return "";
        return tag_text.substr(eq, end - eq);
    }
    return "";
}

static std::vector<std::string> xml_find_tags(const std::string& xml, const std::string& tag) {
    std::vector<std::string> out;
    std::string open = "<" + tag;
    size_t pos = 0;
    while ((pos = xml.find(open, pos)) != std::string::npos) {
        size_t after = pos + open.size();
        if (after >= xml.size()) break;
        char nx = xml[after];
        if (nx != ' ' && nx != '\t' && nx != '>' && nx != '/' && nx != '\r' && nx != '\n')
            { ++pos; continue; }
        size_t end = xml.find('>', after);
        if (end == std::string::npos) break;
        out.push_back(xml.substr(pos, end - pos + 1));
        pos = end + 1;
    }
    return out;
}

static void write_file(const fs::path& p, const std::string& s) {
    fs::create_directories(p.parent_path());
    std::ofstream f(p, std::ios::binary | std::ios::trunc);
    if (!f) die("cannot write " + p.string());
    f.write(s.data(), (std::streamsize)s.size());
}

static std::string read_file(const fs::path& p) {
    std::ifstream f(p, std::ios::binary);
    if (!f) die("cannot read " + p.string());
    std::stringstream ss; ss << f.rdbuf();
    return ss.str();
}

// ---------- path mapping ----------

// QAR paths come in two shapes:
//   /Assets/<chunk>/<rest>   - chunk-routed; needs "release/" injected after chunk
//                              and "#windx11/" injected before <file> for
//                              .fpk / .fpkd / .pftxs (the platform-routed pack
//                              archives that live in #windx11/ on disk).
//   /<rest>                  - everything else (e.g. /Tpp/start.lua) maps
//                              directly to <game_root>/<rest>. These are
//                              files that the unpacker dropped verbatim.
fs::path resolve_qar_path(std::string qar, const fs::path& game_root) {
    for (auto& c : qar) if (c == '\\') c = '/';
    if (qar.empty()) die("empty qar path");

    bool assets = starts_with(qar, "/Assets/") || starts_with(qar, "Assets/");
    if (!assets) {
        if (qar[0] == '/') qar = qar.substr(1);
        fs::path p = game_root;
        for (auto& s : split(qar, '/')) if (!s.empty()) p /= s;
        return p;
    }

    qar = starts_with(qar, "/Assets/") ? qar.substr(8) : qar.substr(7);
    auto parts = split(qar, '/');
    if (parts.empty()) die("empty qar path under /Assets/");

    std::vector<std::string> out;
    out.push_back(parts[0]);                       // chunk (tpp / fox / tpptest)
    out.push_back("release");
    for (size_t i = 1; i + 1 < parts.size(); ++i) out.push_back(parts[i]);

    const std::string& last = parts.back();
    bool platform = ends_with(last, ".fpk") || ends_with(last, ".fpkd") || ends_with(last, ".pftxs");
    if (platform) out.push_back("#windx11");
    out.push_back(last);

    fs::path p = game_root;
    for (auto& s : out) p /= s;
    return p;
}

bool qar_is_fpk(const std::string& qar) {
    return ends_with(qar, ".fpk") || ends_with(qar, ".fpkd");
}

// ---------- dictionary maintenance ----------
//
// Both dict files live next to the game exe, mirroring the FoxKit PathServer
// layout so a fallback resolver pointed at the game folder finds them:
//
//   PathDictionary.txt
//     vpaths with their extension stripped at the first '.' of the final
//     filename segment. PathCode's base hash uses only that portion, so
//     multi-dot names like "ih_general.fre.lng2" collapse to "ih_general"
//     and one entry resolves every variant.
//
//   ExplicitPathDictionary.txt
//     "0x<16hex>\t<vpath>" rows for entries where the metadata supplied a
//     pre-computed StrCode64 hash (QarEntry Hash="..."). This lets the
//     resolver look up by full path-code without re-hashing.
static std::string dict_base(std::string p) {
    for (auto& c : p) if (c == '\\') c = '/';
    if (p.empty()) return p;
    size_t slash = p.find_last_of('/');
    size_t name_start = (slash == std::string::npos) ? 0 : slash + 1;
    size_t dot = p.find('.', name_start);
    if (dot != std::string::npos) p.resize(dot);
    return p;
}

static std::string to_hex16(uint64_t v) {
    char buf[19]; std::snprintf(buf, sizeof(buf), "0x%016llx", (unsigned long long)v);
    return buf;
}

static std::set<std::string> read_lines_set(const fs::path& p) {
    std::set<std::string> have;
    if (!fs::exists(p)) return have;
    std::ifstream f(p); std::string line;
    while (std::getline(f, line)) {
        if (!line.empty() && line.back() == '\r') line.pop_back();
        if (!line.empty()) have.insert(line);
    }
    return have;
}

// Existing explicit-dict keys (the hex column) — so we don't double-insert a
// hash even if its vpath text differs slightly between mods.
static std::set<std::string> read_explicit_hex_keys(const fs::path& p) {
    std::set<std::string> have;
    if (!fs::exists(p)) return have;
    std::ifstream f(p); std::string line;
    while (std::getline(f, line)) {
        if (!line.empty() && line.back() == '\r') line.pop_back();
        auto tab = line.find('\t');
        if (tab == std::string::npos) continue;
        std::string k = line.substr(0, tab);
        for (auto& c : k) c = (char)std::tolower((unsigned char)c);
        have.insert(k);
    }
    return have;
}

size_t update_dictionary_from_mod(const ModInfo& m) {
    if (g_state.game_root.empty() || !fs::exists(g_state.game_root)) return 0;

    fs::path pd = path_dict_file();
    fs::path ed = explicit_dict_file();

    // --- PathDictionary.txt: union of all vpaths the mod mentions, stripped.
    std::set<std::string> have_paths = read_lines_set(pd);
    std::vector<std::string> add_paths;
    auto consider = [&](const std::string& raw) {
        std::string b = dict_base(raw);
        if (b.empty()) return;
        if (have_paths.insert(b).second) add_paths.push_back(b);
    };
    for (auto& q : m.qar_paths) consider(q);
    for (auto& [host, inners] : m.fpk_entries) {
        consider(host);
        for (auto& i : inners) consider(i);
    }

    if (!add_paths.empty()) {
        std::ofstream f(pd, std::ios::app);
        if (!f) die("cannot write " + pd.string());
        for (auto& s : add_paths) f << s << "\n";
    }

    // --- ExplicitPathDictionary.txt: any QarEntry with a Hash attribute.
    std::set<std::string> have_hex = read_explicit_hex_keys(ed);
    std::vector<std::string> add_expl;
    for (auto& [vpath, hash_dec] : m.qar_hashes) {
        if (hash_dec.empty()) continue;
        uint64_t h = 0;
        try { h = std::stoull(hash_dec); } catch (...) { continue; }
        std::string hex = to_hex16(h);
        if (have_hex.insert(hex).second) add_expl.push_back(hex + "\t" + vpath);
    }
    if (!add_expl.empty()) {
        std::ofstream f(ed, std::ios::app);
        if (!f) die("cannot write " + ed.string());
        for (auto& s : add_expl) f << s << "\n";
    }

    return add_paths.size() + add_expl.size();
}

size_t rebuild_dictionary() {
    if (g_state.game_root.empty()) die("game_root not set");
    size_t total = 0;
    for (auto& m : g_state.mods) {
        // Re-parse metadata.xml so we pick up QarEntry Hash attrs that state.txt
        // may not have recorded (mods added before hash tracking existed).
        fs::path unp  = mod_unpack_dir(m.id);
        fs::path meta = unp / "metadata.xml";
        if (fs::exists(meta)) {
            ModInfo tmp; tmp.id = m.id;
            parse_metadata(meta, tmp);
            for (auto& [vp, h] : tmp.qar_hashes)
                if (!h.empty()) m.qar_hashes.insert({vp, h});
        }
        total += update_dictionary_from_mod(m);
    }
    save_state();
    return total;
}

// ---------- state I/O ----------

void load_state() {
    g_state = {};
    if (!fs::exists(g_state_path)) return;
    std::ifstream f(g_state_path);
    std::string line; ModInfo* cur = nullptr;
    while (std::getline(f, line)) {
        line = trim(line);
        if (line.empty() || line[0] == '#') continue;
        if (starts_with(line, "game_root=")) { g_state.game_root = line.substr(10); cur = nullptr; }
        else if (starts_with(line, "datfpk="))   { g_state.datfpk    = line.substr(7);  cur = nullptr; }
        else if (line == "[mod]")                { g_state.mods.emplace_back(); cur = &g_state.mods.back(); }
        else if (cur) {
            auto eq = line.find('=');
            if (eq == std::string::npos) continue;
            std::string k = line.substr(0, eq), v = line.substr(eq + 1);
            if      (k == "id")      cur->id = v;
            else if (k == "name")    cur->name = v;
            else if (k == "version") cur->version = v;
            else if (k == "author")  cur->author = v;
            else if (k == "enabled") cur->enabled = (v == "1" || v == "true");
            else if (k == "source")  cur->source = v;
            else if (k == "qar")     cur->qar_paths.push_back(v);
            else if (k == "qarhash") {
                auto bar = v.find('|');
                if (bar != std::string::npos)
                    cur->qar_hashes[v.substr(0, bar)] = v.substr(bar + 1);
            }
            else if (k == "gamedir") cur->gamedir_entries.push_back(v);
            else if (k == "fpk") {
                auto bar = v.find('|');
                if (bar != std::string::npos)
                    cur->fpk_entries[v.substr(0, bar)].push_back(v.substr(bar + 1));
            }
        }
    }
}

void save_state() {
    fs::create_directories(g_state_path.parent_path());
    std::ofstream f(g_state_path, std::ios::trunc);
    f << "# mgsv_modmgr state. Edit load order by reordering [mod] blocks.\n";
    f << "game_root=" << g_state.game_root.string() << "\n";
    f << "datfpk="    << g_state.datfpk.string()    << "\n";
    for (auto& m : g_state.mods) {
        f << "\n[mod]\n";
        f << "id="      << m.id      << "\n";
        f << "name="    << m.name    << "\n";
        f << "version=" << m.version << "\n";
        f << "author="  << m.author  << "\n";
        f << "enabled=" << (m.enabled ? "1" : "0") << "\n";
        f << "source="  << m.source.string() << "\n";
        for (auto& q : m.qar_paths)        f << "qar=" << q << "\n";
        for (auto& [q, h] : m.qar_hashes)  f << "qarhash=" << q << "|" << h << "\n";
        for (auto& g : m.gamedir_entries)  f << "gamedir=" << g << "\n";
        for (auto& [host, inners] : m.fpk_entries)
            for (auto& i : inners) f << "fpk=" << host << "|" << i << "\n";
    }
}

// ---------- helpers used by commands ----------

static void check_init() {
    if (g_state.game_root.empty())      die("not initialised. set game_root and datfpk first");
    if (!fs::exists(g_state.game_root)) die("game_root does not exist: " + g_state.game_root.string());
}

static void check_datfpk() {
    if (g_state.datfpk.empty())      die("datfpk path not set");
    if (!fs::exists(g_state.datfpk)) die("datfpk not found: " + g_state.datfpk.string());
}

static fs::path mod_unpack_dir(const std::string& id) { return g_tmp_dir / ("mod_" + id); }

static void extract_zip(const fs::path& zip, const fs::path& out_dir) {
    fs::create_directories(out_dir);
    std::string cmd = "tar.exe -xf " + quote(zip) + " -C " + quote(out_dir);
    if (run_process(cmd) != 0) die("zip extract failed: " + zip.string());
}

// Walks the extracted mod directory for files under any case-folded "GameDir/"
// component and produces relative paths beneath it.
static void scan_gamedir(const fs::path& mod_root, std::vector<std::string>& out) {
    if (!fs::exists(mod_root)) return;
    for (auto& e : fs::recursive_directory_iterator(mod_root)) {
        if (!e.is_regular_file()) continue;
        fs::path rel = fs::relative(e.path(), mod_root);
        std::string s = rel.generic_string();
        std::string ls = to_lower(s);
        const std::string marker = "gamedir/";
        auto pos = ls.find(marker);
        if (pos == std::string::npos) continue;
        // Allow GameDir to be either at the top or nested (e.g. Assets/.../GameDir/x).
        // The portion AFTER GameDir/ is the rel path under game_root.
        std::string rest = s.substr(pos + marker.size());
        if (!rest.empty()) out.push_back(rest);
    }
}

static void parse_metadata(const fs::path& meta_path, ModInfo& m) {
    std::string xml = read_file(meta_path);
    auto entries = xml_find_tags(xml, "ModEntry");
    if (!entries.empty()) {
        m.name    = xml_attr(entries[0], "Name");
        m.version = xml_attr(entries[0], "Version");
        m.author  = xml_attr(entries[0], "Author");
    }
    for (auto& t : xml_find_tags(xml, "QarEntry")) {
        auto p = xml_attr(t, "FilePath");
        if (p.empty()) continue;
        m.qar_paths.push_back(p);
        auto h = xml_attr(t, "Hash");
        if (!h.empty()) m.qar_hashes[p] = h;
    }
    for (auto& t : xml_find_tags(xml, "FpkEntry")) {
        auto host = xml_attr(t, "FpkFile"); auto inner = xml_attr(t, "FilePath");
        if (!host.empty() && !inner.empty()) m.fpk_entries[host].push_back(inner);
    }
    // Some mods declare loose files via <FileEntry FilePath="..."/>; treat the
    // path as game-root-relative (drop a leading slash).
    for (auto& t : xml_find_tags(xml, "FileEntry")) {
        auto p = xml_attr(t, "FilePath");
        if (p.empty()) continue;
        if (p[0] == '/') p = p.substr(1);
        m.gamedir_entries.push_back(p);
    }
}

// ---------- baseline cache / revert ----------

// First time the manager has to modify a game file, it snapshots the pristine
// copy under <exe>/root/<rel>. The snapshot is never overwritten; every Apply
// rebuilds from it. Files that did not exist pre-mod get a sibling .absent
// marker so revert removes them rather than restoring stale data.
static fs::path baseline_for(const fs::path& disk_path) {
    fs::path rel = fs::relative(disk_path, g_state.game_root);
    return g_baseline_dir / rel;
}

static void ensure_baseline(const fs::path& disk_path) {
    fs::path bak = baseline_for(disk_path);
    fs::path marker = bak.string() + ".absent";
    if (fs::exists(bak) || fs::exists(marker)) return;
    fs::create_directories(bak.parent_path());
    if (fs::exists(disk_path)) fs::copy_file(disk_path, bak);
    else {
        std::ofstream o(marker, std::ios::trunc); o << "absent\n";
    }
}

// ---------- merge engine for one host fpk(d) ----------

static fs::path datfpk_unpack_fpk(const fs::path& fpk_path, const fs::path& work_dir) {
    fs::create_directories(work_dir);
    fs::path local = work_dir / fpk_path.filename();
    fs::copy_file(fpk_path, local, fs::copy_options::overwrite_existing);
    std::string cmd = quote(g_state.datfpk) + " " + quote(local);
    if (run_process(cmd) != 0) die("datfpk unpack failed: " + local.string());
    return local;
}

static void datfpk_pack_fpk(const fs::path& json, const fs::path& out_path, const fs::path& input_dir) {
    std::string cmd = quote(g_state.datfpk) + " " + quote(json) + " " + quote(out_path) + " " + quote(input_dir);
    if (run_process(cmd) != 0) die("datfpk pack failed: " + json.string());
}

static void copy_tree_overlay(const fs::path& src, const fs::path& dst) {
    if (!fs::exists(src)) return;
    for (auto& e : fs::recursive_directory_iterator(src)) {
        if (!e.is_regular_file()) continue;
        fs::path rel = fs::relative(e.path(), src);
        fs::path tgt = dst / rel;
        fs::create_directories(tgt.parent_path());
        fs::copy_file(e.path(), tgt, fs::copy_options::overwrite_existing);
    }
}

// ---------- minimal JSON edit for fpk(d) defs ----------
// datfpk's json is { "type": "fpk|fpkd", "entries":[{"filePath":...}],
//                    "references":[{"filePath":...}] }
// We regenerate `entries` from the actual merged tree (so mod-added files are
// included), keep `type` from the base, and union `references` across base +
// every contributing mod's fpk json.

static std::string extract_type(const std::string& json) {
    auto p = json.find("\"type\"");
    if (p == std::string::npos) return "fpkd";
    auto c = json.find(':', p); if (c == std::string::npos) return "fpkd";
    auto q1 = json.find('"', c); if (q1 == std::string::npos) return "fpkd";
    auto q2 = json.find('"', q1 + 1); if (q2 == std::string::npos) return "fpkd";
    return json.substr(q1 + 1, q2 - q1 - 1);
}

static std::vector<std::string> extract_refs(const std::string& json) {
    std::vector<std::string> out;
    auto p = json.find("\"references\"");
    if (p == std::string::npos) return out;
    auto lb = json.find('[', p); if (lb == std::string::npos) return out;
    auto rb = json.find(']', lb); if (rb == std::string::npos) return out;
    std::string body = json.substr(lb + 1, rb - lb - 1);
    size_t i = 0;
    while ((i = body.find("\"filePath\"", i)) != std::string::npos) {
        auto c = body.find(':', i); if (c == std::string::npos) break;
        auto q1 = body.find('"', c); if (q1 == std::string::npos) break;
        auto q2 = body.find('"', q1 + 1); if (q2 == std::string::npos) break;
        out.push_back(body.substr(q1 + 1, q2 - q1 - 1));
        i = q2 + 1;
    }
    return out;
}

static std::string make_fpk_json(const std::string& type,
                                 const std::vector<std::string>& entries,
                                 const std::vector<std::string>& refs) {
    std::stringstream ss;
    ss << "{\n  \"type\": \"" << type << "\",\n  \"entries\": [";
    for (size_t i = 0; i < entries.size(); ++i)
        ss << (i ? ",\n" : "\n") << "    { \"filePath\": \"" << entries[i] << "\" }";
    ss << (entries.empty() ? "" : "\n  ") << "]";
    if (!refs.empty()) {
        ss << ",\n  \"references\": [";
        for (size_t i = 0; i < refs.size(); ++i)
            ss << (i ? ",\n" : "\n") << "    { \"filePath\": \"" << refs[i] << "\" }";
        ss << "\n  ]";
    }
    ss << "\n}\n";
    return ss.str();
}

// Walk an extracted fpk directory, return every regular file as a QAR-style
// "/Assets/..." path (forward slashes, leading '/').
static std::vector<std::string> walk_to_entries(const fs::path& root) {
    std::vector<std::string> out;
    if (!fs::exists(root)) return out;
    for (auto& e : fs::recursive_directory_iterator(root)) {
        if (!e.is_regular_file()) continue;
        std::string rel = fs::relative(e.path(), root).generic_string();
        if (rel.empty()) continue;
        out.push_back("/" + rel);
    }
    std::sort(out.begin(), out.end());
    return out;
}

// ---------- raw (non-fpk) QarEntry handling ----------

// Locate the mod's source file for a given qar path. SnakeBite zips put the
// payload at the qar path itself (minus leading '/').
static fs::path mod_payload_for(const ModInfo& m, const std::string& qar_path) {
    std::string inner = qar_path;
    if (!inner.empty() && inner[0] == '/') inner.erase(0, 1);
    fs::path mod_root = mod_unpack_dir(m.id);
    if (!fs::exists(mod_root)) extract_zip(m.source, mod_root);
    fs::path p = mod_root / inner;
    return p;
}

static void rebuild_raw(const std::string& qar_path, const fs::path& disk_path) {
    log("");
    log("== copy " + qar_path);
    log("   disk: " + disk_path.string());

    // Last enabled mod that ships this path wins.
    const ModInfo* winner = nullptr;
    fs::path       src;
    for (auto& m : g_state.mods) {
        if (!m.enabled) continue;
        if (std::find(m.qar_paths.begin(), m.qar_paths.end(), qar_path) == m.qar_paths.end()) continue;
        fs::path p = mod_payload_for(m, qar_path);
        if (!fs::exists(p)) continue;
        winner = &m; src = p;
    }
    if (!winner) { log("   no enabled mod ships this path; restoring original"); return; }

    ensure_baseline(disk_path);
    fs::create_directories(disk_path.parent_path());
    fs::copy_file(src, disk_path, fs::copy_options::overwrite_existing);
    log("   from mod: " + winner->id);
    log("   -> wrote " + disk_path.string());
}

// ---------- fpk(d) rebuild ----------

static void rebuild_host(const std::string& qar_path, const fs::path& disk_path) {
    log("");
    log("== rebuild fpk " + qar_path);
    log("   disk: " + disk_path.string());

    fs::path work = g_tmp_dir / ("host_" + disk_path.filename().string());
    fs::remove_all(work);
    fs::create_directories(work);

    // Snapshot the baseline first, then ask the cache what the baseline was.
    // The disk file may exist purely because a previous (partial) apply wrote
    // it, so we must NOT use disk state to decide. The .absent marker (or its
    // absence) is the authoritative record.
    ensure_baseline(disk_path);
    fs::path bak    = baseline_for(disk_path);
    fs::path marker = fs::path(bak.string() + ".absent");
    bool     baseline_existed = fs::exists(bak);

    fs::path  extracted;
    std::string  type    = "fpkd";
    std::vector<std::string> refs_union;
    std::string  out_ext = disk_path.extension().string();  // ".fpkd" or ".fpk"

    if (baseline_existed) {
        fs::path local = datfpk_unpack_fpk(bak, work);
        fs::path json  = local.string() + ".json";
        if (!fs::exists(json)) die("expected json not found: " + json.string());
        std::string txt = read_file(json);
        type = extract_type(txt);
        for (auto& r : extract_refs(txt)) refs_union.push_back(r);
        std::string ext = local.extension().string(), stem = local.stem().string();
        extracted = work / (stem + "_" + ext.substr(1));
        if (!fs::exists(extracted)) fs::create_directories(extracted);  // empty fpk
    } else {
        // New host (mod-introduced). The .absent marker was written above.
        // Bootstrap the extract dir from the FIRST enabled contributor's fpk.
        const ModInfo* first = nullptr; fs::path first_payload;
        for (auto& m : g_state.mods) {
            if (!m.enabled) continue;
            if (std::find(m.qar_paths.begin(), m.qar_paths.end(), qar_path) == m.qar_paths.end()) continue;
            fs::path p = mod_payload_for(m, qar_path);
            if (!fs::exists(p)) continue;
            first = &m; first_payload = p; break;
        }
        if (!first) { log("   no enabled contributor ships a payload; skipping"); return; }
        log("   bootstrapping from " + first->id);
        fs::path local = datfpk_unpack_fpk(first_payload, work);
        fs::path json  = local.string() + ".json";
        if (fs::exists(json)) {
            std::string txt = read_file(json);
            type = extract_type(txt);
            for (auto& r : extract_refs(txt)) refs_union.push_back(r);
        }
        std::string ext = local.extension().string(), stem = local.stem().string();
        extracted = work / (stem + "_" + ext.substr(1));
        if (!fs::exists(extracted)) fs::create_directories(extracted);  // empty fpk bootstrap
    }

    // Overlay every enabled mod that names this host. (The bootstrap mod is
    // re-processed harmlessly — its files just overwrite themselves.)
    for (auto& m : g_state.mods) {
        if (!m.enabled) continue;
        if (std::find(m.qar_paths.begin(), m.qar_paths.end(), qar_path) == m.qar_paths.end()) continue;
        fs::path mod_fpk = mod_payload_for(m, qar_path);
        if (!fs::exists(mod_fpk)) { log("   WARN: mod missing payload: " + m.id); continue; }
        log("   overlay: " + m.id);

        fs::path mod_work = work / ("from_" + m.id);
        fs::create_directories(mod_work);
        fs::path mod_local = datfpk_unpack_fpk(mod_fpk, mod_work);
        std::string mext = mod_local.extension().string(), mstem = mod_local.stem().string();
        fs::path mod_extracted = mod_work / (mstem + "_" + mext.substr(1));
        if (!fs::exists(mod_extracted)) {
            // empty mod fpk — still pull its references below
        } else {
            copy_tree_overlay(mod_extracted, extracted);
        }

        fs::path mod_json = mod_local.string() + ".json";
        if (fs::exists(mod_json))
            for (auto& r : extract_refs(read_file(mod_json))) refs_union.push_back(r);
    }

    // Dedup references.
    std::sort(refs_union.begin(), refs_union.end());
    refs_union.erase(std::unique(refs_union.begin(), refs_union.end()), refs_union.end());

    // Regenerate the entries list from the actual merged filesystem state.
    auto entries = walk_to_entries(extracted);
    std::string new_json = make_fpk_json(type, entries, refs_union);
    fs::path merged_json = work / ("merged" + out_ext + ".json");
    write_file(merged_json, new_json);

    fs::path out_tmp = work / ("out" + out_ext);
    datfpk_pack_fpk(merged_json, out_tmp, extracted);
    fs::create_directories(disk_path.parent_path());
    fs::copy_file(out_tmp, disk_path, fs::copy_options::overwrite_existing);
    log("   -> wrote " + disk_path.string() + "  (entries=" + std::to_string(entries.size()) +
        ", refs=" + std::to_string(refs_union.size()) + ")");
}

// ---------- GameDir handling ----------

static void apply_gamedir_for_mod(const ModInfo& m) {
    if (m.gamedir_entries.empty()) return;
    log("   gamedir files: " + std::to_string(m.gamedir_entries.size()));

    fs::path mod_root = mod_unpack_dir(m.id);
    if (!fs::exists(mod_root)) extract_zip(m.source, mod_root);

    for (auto& rel : m.gamedir_entries) {
        // Source: locate the file in the mod's extracted tree. Prefer
        // mod_root/GameDir/<rel>, fall back to mod_root/<rel>.
        fs::path src = mod_root / "GameDir" / rel;
        if (!fs::exists(src)) src = mod_root / rel;
        if (!fs::exists(src)) {
            // case-insensitive scan for any "*/GameDir/<rel>"
            for (auto& e : fs::recursive_directory_iterator(mod_root)) {
                if (!e.is_regular_file()) continue;
                std::string p = e.path().generic_string();
                std::string lp = to_lower(p);
                std::string suf = "/gamedir/" + to_lower(rel);
                if (lp.size() >= suf.size() &&
                    lp.compare(lp.size() - suf.size(), suf.size(), suf) == 0) {
                    src = e.path(); break;
                }
            }
        }
        if (!fs::exists(src)) { log("   WARN: gamedir source missing for " + rel); continue; }

        fs::path dst = g_state.game_root / rel;
        ensure_baseline(dst);
        fs::create_directories(dst.parent_path());
        fs::copy_file(src, dst, fs::copy_options::overwrite_existing);
        log("   gamedir -> " + dst.string());
    }
}

// ---------- public commands ----------

void cmd_init(const fs::path& game_root, const fs::path& datfpk) {
    g_state.game_root = fs::absolute(game_root);
    g_state.datfpk    = fs::absolute(datfpk);
    save_state();
    log("initialised. game_root=" + g_state.game_root.string() + "  datfpk=" + g_state.datfpk.string());
}

void add_mod(const fs::path& mgsv_path) {
    check_init();
    fs::path src = fs::absolute(mgsv_path);
    if (!fs::exists(src)) die("no such file: " + src.string());

    std::string id = to_lower(src.stem().string());
    for (auto& m : g_state.mods) if (m.id == id) die("mod id already registered: " + id);

    fs::create_directories(g_mods_dir);
    fs::path stored = g_mods_dir / (id + ".mgsv");
    fs::copy_file(src, stored, fs::copy_options::overwrite_existing);

    fs::path unp = mod_unpack_dir(id);
    fs::remove_all(unp);
    extract_zip(stored, unp);

    ModInfo m;
    m.id = id; m.enabled = true; m.source = stored;
    fs::path meta = unp / "metadata.xml";
    if (fs::exists(meta)) parse_metadata(meta, m);
    else log("WARN: no metadata.xml in mod");

    // Always rescan the extracted tree for a literal GameDir/ folder — older
    // SnakeBite mods just dump it there with no <FileEntry> entries.
    scan_gamedir(unp, m.gamedir_entries);
    // Dedup gamedir_entries.
    std::sort(m.gamedir_entries.begin(), m.gamedir_entries.end());
    m.gamedir_entries.erase(std::unique(m.gamedir_entries.begin(), m.gamedir_entries.end()),
                            m.gamedir_entries.end());

    g_state.mods.push_back(m);
    save_state();
    size_t dict_added = update_dictionary_from_mod(m);
    log("added: " + m.id + "  (" + m.name + " v" + m.version + ")");
    log("  qar entries:     " + std::to_string(m.qar_paths.size()));
    log("  fpk entries:     " + std::to_string(m.fpk_entries.size()));
    log("  gamedir entries: " + std::to_string(m.gamedir_entries.size()));
    log("  dictionary +" + std::to_string(dict_added) + " new path(s)");
}

void enable_mod(const std::string& id, bool on) {
    for (auto& m : g_state.mods) if (m.id == id) { m.enabled = on; save_state(); return; }
    die("no such mod id: " + id);
}

void remove_mod(const std::string& id) {
    auto it = std::find_if(g_state.mods.begin(), g_state.mods.end(),
                           [&](const ModInfo& m){ return m.id == id; });
    if (it == g_state.mods.end()) die("no such mod id: " + id);
    if (fs::exists(it->source)) fs::remove(it->source);
    g_state.mods.erase(it);
    save_state();
}

void move_mod(const std::string& id, int delta) {
    auto it = std::find_if(g_state.mods.begin(), g_state.mods.end(),
                           [&](const ModInfo& m){ return m.id == id; });
    if (it == g_state.mods.end()) die("no such mod id: " + id);
    size_t i = (size_t)(it - g_state.mods.begin());
    long long j = (long long)i + delta;
    if (j < 0 || j >= (long long)g_state.mods.size()) return;
    std::swap(g_state.mods[i], g_state.mods[(size_t)j]);
    save_state();
}

void apply_all() {
    check_init();
    check_datfpk();
    fs::create_directories(g_tmp_dir);

    std::set<std::string> hosts;
    for (auto& m : g_state.mods)
        for (auto& q : m.qar_paths) hosts.insert(q);

    size_t fpk_done = 0, raw_done = 0;
    for (auto& qar : hosts) {
        fs::path disk = resolve_qar_path(qar, g_state.game_root);
        if (qar_is_fpk(qar)) { rebuild_host(qar, disk); ++fpk_done; }
        else                 { rebuild_raw(qar, disk);  ++raw_done; }
    }
    log("");
    log("fpk hosts: " + std::to_string(fpk_done) + ", raw files: " + std::to_string(raw_done));

    // GameDir / loose-file overlays last so they win any conflicts.
    for (auto& m : g_state.mods) {
        if (!m.enabled || m.gamedir_entries.empty()) continue;
        log("");
        log("== gamedir overlay for " + m.id);
        apply_gamedir_for_mod(m);
    }
    log("");
    log("apply complete. tmp left at " + g_tmp_dir.string());
}

void revert_all() {
    check_init();
    if (!fs::exists(g_baseline_dir)) { log("no original-file cache; nothing to revert"); return; }
    size_t restored = 0, deleted = 0;
    for (auto& e : fs::recursive_directory_iterator(g_baseline_dir)) {
        if (!e.is_regular_file()) continue;
        fs::path rel = fs::relative(e.path(), g_baseline_dir);
        if (rel.extension() == ".absent") {
            fs::path orig_rel = rel; orig_rel.replace_extension("");
            fs::path disk = g_state.game_root / orig_rel;
            if (fs::exists(disk)) { fs::remove(disk); ++deleted; }
        } else {
            fs::path disk = g_state.game_root / rel;
            fs::create_directories(disk.parent_path());
            fs::copy_file(e.path(), disk, fs::copy_options::overwrite_existing);
            ++restored;
        }
    }
    log("reverted " + std::to_string(restored) + " file(s), removed " +
        std::to_string(deleted) + " mod-added file(s)");
}

} // namespace core
