// mgsv_modmgr CLI entry point.
#include "core.h"

#include <cstdio>
#include <iostream>
#include <stdexcept>
#include <string>

static void usage() {
    std::cout <<
        "mgsv_modmgr CLI\n"
        "\n"
        "  modmgr init <game-root> <datfpk.exe>\n"
        "  modmgr add <mod.mgsv>\n"
        "  modmgr list\n"
        "  modmgr enable <id>\n"
        "  modmgr disable <id>\n"
        "  modmgr remove <id>\n"
        "  modmgr up <id>          move mod earlier in load order\n"
        "  modmgr down <id>        move mod later  in load order\n"
        "  modmgr apply\n"
        "  modmgr revert\n"
        "  modmgr resolve <qar-path>\n"
        "  modmgr dict-rebuild     rebuild dictionary.txt from all registered mods\n";
}

int main(int argc, char** argv) {
    core::init_paths();
    core::set_logger([](const std::string& s){ std::cout << s << "\n"; });
    core::load_state();

    if (argc < 2) { usage(); return 0; }
    std::string c = argv[1];
    try {
        if (c == "init") {
            if (argc < 4) { usage(); return 1; }
            core::cmd_init(argv[2], argv[3]);
        }
        else if (c == "add") {
            if (argc < 3) { usage(); return 1; }
            core::add_mod(argv[2]);
        }
        else if (c == "list") {
            if (core::g_state.mods.empty()) { std::cout << "no mods registered\n"; return 0; }
            std::cout << "game_root = " << core::g_state.game_root << "\n";
            std::cout << "datfpk    = " << core::g_state.datfpk    << "\n\n";
            int i = 0;
            for (auto& m : core::g_state.mods) {
                std::cout << "[" << i++ << "] " << (m.enabled ? "ON  " : "off ")
                          << m.id << "   " << m.name << " v" << m.version
                          << " (" << m.author << ")\n";
                for (auto& q : m.qar_paths)       std::cout << "      qar:     " << q << "\n";
                for (auto& g : m.gamedir_entries) std::cout << "      gamedir: " << g << "\n";
            }
        }
        else if (c == "enable")  { if (argc < 3) { usage(); return 1; } core::enable_mod(argv[2], true); }
        else if (c == "disable") { if (argc < 3) { usage(); return 1; } core::enable_mod(argv[2], false); }
        else if (c == "remove")  { if (argc < 3) { usage(); return 1; } core::remove_mod(argv[2]); }
        else if (c == "up")      { if (argc < 3) { usage(); return 1; } core::move_mod(argv[2], -1); }
        else if (c == "down")    { if (argc < 3) { usage(); return 1; } core::move_mod(argv[2], +1); }
        else if (c == "apply")   { core::apply_all(); }
        else if (c == "revert")  { core::revert_all(); }
        else if (c == "resolve") {
            if (argc < 3) { usage(); return 1; }
            std::cout << core::resolve_qar_path(argv[2], core::g_state.game_root).string() << "\n";
        }
        else if (c == "dict-rebuild") {
            size_t n = core::rebuild_dictionary();
            std::cout << "dictionary +" << n << " new entr(ies) -> " << core::g_state.game_root.string() << "\n";
        }
        else { usage(); return 1; }
    } catch (const std::exception& e) {
        std::cerr << "error: " << e.what() << "\n";
        return 1;
    }
    return 0;
}
