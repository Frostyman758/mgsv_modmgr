// mgsv_modmgr core: state, mod parsing, merge engine.
// Both the CLI and GUI front-ends consume this.
#pragma once

#include <filesystem>
#include <functional>
#include <map>
#include <set>
#include <string>
#include <vector>

namespace fs = std::filesystem;

struct ModInfo {
    std::string id;
    std::string name;
    std::string version;
    std::string author;
    bool        enabled = true;
    fs::path    source;                                                 // copy in mods/
    std::vector<std::string>                            qar_paths;      // host fpk(d)s the mod replaces
    std::map<std::string, std::string>                  qar_hashes;     // qar_path -> decimal uint64 (StrCode64) if metadata had Hash=
    std::map<std::string, std::vector<std::string>>    fpk_entries;     // host -> inner files (descriptive)
    std::vector<std::string>                            gamedir_entries; // rel paths under GameDir/, target <game_root>/<rel>
};

struct State {
    fs::path             game_root;
    fs::path             datfpk;
    std::vector<ModInfo> mods;
};

using LogFn = std::function<void(const std::string&)>;

namespace core {

extern State    g_state;
extern fs::path g_exe_dir;
extern fs::path g_state_path;
extern fs::path g_baseline_dir;
extern fs::path g_mods_dir;
extern fs::path g_tmp_dir;

void  init_paths();
void  set_logger(LogFn fn);
void  log(const std::string& s);

void  load_state();
void  save_state();

// Mutators. Throw std::runtime_error on failure.
void  cmd_init(const fs::path& game_root, const fs::path& datfpk);
void  add_mod(const fs::path& mgsv_path);
void  enable_mod(const std::string& id, bool on);
void  remove_mod(const std::string& id);
void  move_mod(const std::string& id, int delta);     // -1 = up, +1 = down
void  apply_all();
void  revert_all();

fs::path resolve_qar_path(std::string qar, const fs::path& game_root);
bool     qar_is_fpk(const std::string& qar);

// Append any virtual paths the mod ships (Qar + Fpk inner) to dictionary.txt,
// stripping each path to its PathCode base (everything before the first '.'
// in the final filename segment) and deduping against what's already there.
// Returns count of newly appended entries.
size_t   update_dictionary_from_mod(const ModInfo& m);
size_t   rebuild_dictionary();   // re-runs update_dictionary_from_mod for every registered mod

} // namespace core
