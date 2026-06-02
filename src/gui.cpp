#include "core.h"

#include <algorithm>
#include <atomic>
#include <string>
#include <thread>
#include <vector>

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <commctrl.h>
#include <shobjidl.h>
#include <shlobj.h>
#include <dwmapi.h>
#include <uxtheme.h>

#pragma comment(lib, "comctl32.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "dwmapi.lib")
#pragma comment(lib, "uxtheme.lib")

// ---- IDs ----
#define IDC_LIST       1001
#define IDC_LOG        1002
#define IDC_STATUS     1003
#define IDC_TOOLBAR    1004
#define IDC_BTN_ADD    1101
#define IDC_BTN_REMOVE 1102
#define IDC_BTN_UP     1103
#define IDC_BTN_DOWN   1104
#define IDC_BTN_APPLY  1105
#define IDC_BTN_REVERT 1106
#define IDC_BTN_SETUP  1107

#define WM_APP_LOG     (WM_APP + 1)

// ---- palette (BG3MM-ish dark) ----
namespace col {
    constexpr COLORREF bg        = RGB(0x1E, 0x1E, 0x22);
    constexpr COLORREF panel     = RGB(0x26, 0x26, 0x2B);
    constexpr COLORREF panel_alt = RGB(0x2A, 0x2A, 0x30);
    constexpr COLORREF border    = RGB(0x3A, 0x3A, 0x42);
    constexpr COLORREF text      = RGB(0xE6, 0xE6, 0xEA);
    constexpr COLORREF text_dim  = RGB(0x9A, 0x9A, 0xA4);
    constexpr COLORREF btn       = RGB(0x33, 0x33, 0x3B);
    constexpr COLORREF btn_hover = RGB(0x42, 0x42, 0x4C);
    constexpr COLORREF btn_down  = RGB(0x55, 0x55, 0x62);
    constexpr COLORREF accent    = RGB(0x7B, 0x5C, 0xD6);  // muted violet
    constexpr COLORREF sel       = RGB(0x3D, 0x35, 0x6E);
    constexpr COLORREF sel_unfoc = RGB(0x33, 0x33, 0x3B);
}

static HBRUSH g_brBg     = nullptr;
static HBRUSH g_brPanel  = nullptr;
static HBRUSH g_brBtn    = nullptr;
static HBRUSH g_brBorder = nullptr;
static HFONT  g_fUi      = nullptr;
static HFONT  g_fUiBold  = nullptr;
static HFONT  g_fMono    = nullptr;

static HWND g_hMain   = nullptr;
static HWND g_hList   = nullptr;
static HWND g_hLog    = nullptr;
static HWND g_hStatus = nullptr;
static std::atomic<bool> g_busy{false};

// hover/down tracking for owner-draw buttons
static HWND g_hHover = nullptr;
static HWND g_hPress = nullptr;

// ---- helpers ----

static std::wstring to_w(const std::string& s) {
    if (s.empty()) return {};
    int n = MultiByteToWideChar(CP_UTF8, 0, s.data(), (int)s.size(), nullptr, 0);
    std::wstring w(n, 0);
    MultiByteToWideChar(CP_UTF8, 0, s.data(), (int)s.size(), w.data(), n);
    return w;
}

static std::string from_w(const std::wstring& w) {
    if (w.empty()) return {};
    int n = WideCharToMultiByte(CP_UTF8, 0, w.data(), (int)w.size(), nullptr, 0, nullptr, nullptr);
    std::string s(n, 0);
    WideCharToMultiByte(CP_UTF8, 0, w.data(), (int)w.size(), s.data(), n, nullptr, nullptr);
    return s;
}

static void apply_immersive_dark(HWND hwnd) {
    BOOL on = TRUE;
    // Windows 10 2004+: attr 20.  Older builds (1809..1909) used attr 19.
    if (FAILED(DwmSetWindowAttribute(hwnd, 20, &on, sizeof(on))))
        DwmSetWindowAttribute(hwnd, 19, &on, sizeof(on));
}

static void append_log(const std::string& line) {
    int len = GetWindowTextLengthA(g_hLog);
    SendMessageA(g_hLog, EM_SETSEL, (WPARAM)len, (LPARAM)len);
    std::string with_eol = line + "\r\n";
    SendMessageA(g_hLog, EM_REPLACESEL, FALSE, (LPARAM)with_eol.c_str());
}

static void gui_log(const std::string& s) {
    HLOCAL h = LocalAlloc(LMEM_FIXED, s.size() + 1);
    memcpy(h, s.c_str(), s.size() + 1);
    PostMessageA(g_hMain, WM_APP_LOG, (WPARAM)h, 0);
}

static void set_status() {
    std::string s = "  game_root: " + core::g_state.game_root.string() +
                    "    datfpk: " + core::g_state.datfpk.string();
    SendMessageA(g_hStatus, SB_SETTEXTA, 0 | SBT_NOBORDERS, (LPARAM)s.c_str());
}

static void refresh_list() {
    ListView_DeleteAllItems(g_hList);
    int i = 0;
    for (auto& m : core::g_state.mods) {
        LVITEMA it{}; it.mask = LVIF_TEXT | LVIF_PARAM;
        it.iItem = i; it.iSubItem = 0; it.lParam = i;
        char id_buf[256]; lstrcpynA(id_buf, m.id.c_str(), 256);
        it.pszText = id_buf;
        ListView_InsertItem(g_hList, &it);

        ListView_SetItemText(g_hList, i, 1, (LPSTR)m.name.c_str());
        ListView_SetItemText(g_hList, i, 2, (LPSTR)m.version.c_str());
        ListView_SetItemText(g_hList, i, 3, (LPSTR)m.author.c_str());
        std::string qstr = std::to_string(m.qar_paths.size());
        std::string gstr = std::to_string(m.gamedir_entries.size());
        ListView_SetItemText(g_hList, i, 4, (LPSTR)qstr.c_str());
        ListView_SetItemText(g_hList, i, 5, (LPSTR)gstr.c_str());

        ListView_SetCheckState(g_hList, i, m.enabled ? TRUE : FALSE);
        ++i;
    }
}

static int selected_index() {
    return ListView_GetNextItem(g_hList, -1, LVNI_SELECTED);
}

// ---- file pickers ----

static std::string pick_file(HWND owner, const wchar_t* title, const wchar_t* filter_name,
                             const wchar_t* filter_ext) {
    IFileOpenDialog* dlg = nullptr;
    if (FAILED(CoCreateInstance(CLSID_FileOpenDialog, nullptr, CLSCTX_INPROC_SERVER,
                                IID_PPV_ARGS(&dlg)))) return {};
    COMDLG_FILTERSPEC f[] = { { filter_name, filter_ext } };
    dlg->SetFileTypes(1, f);
    dlg->SetTitle(title);
    std::string out;
    if (SUCCEEDED(dlg->Show(owner))) {
        IShellItem* it = nullptr;
        if (SUCCEEDED(dlg->GetResult(&it))) {
            PWSTR p = nullptr;
            if (SUCCEEDED(it->GetDisplayName(SIGDN_FILESYSPATH, &p))) {
                out = from_w(p); CoTaskMemFree(p);
            }
            it->Release();
        }
    }
    dlg->Release();
    return out;
}

static std::string pick_folder(HWND owner, const wchar_t* title) {
    IFileOpenDialog* dlg = nullptr;
    if (FAILED(CoCreateInstance(CLSID_FileOpenDialog, nullptr, CLSCTX_INPROC_SERVER,
                                IID_PPV_ARGS(&dlg)))) return {};
    DWORD opts = 0; dlg->GetOptions(&opts);
    dlg->SetOptions(opts | FOS_PICKFOLDERS);
    dlg->SetTitle(title);
    std::string out;
    if (SUCCEEDED(dlg->Show(owner))) {
        IShellItem* it = nullptr;
        if (SUCCEEDED(dlg->GetResult(&it))) {
            PWSTR p = nullptr;
            if (SUCCEEDED(it->GetDisplayName(SIGDN_FILESYSPATH, &p))) {
                out = from_w(p); CoTaskMemFree(p);
            }
            it->Release();
        }
    }
    dlg->Release();
    return out;
}

// ---- actions ----

static void do_settings(HWND owner) {
    std::string game = pick_folder(owner, L"Select MGSV:TPP game root");
    if (game.empty()) return;
    std::string dat = pick_file(owner, L"Select datfpk.exe", L"Executables", L"*.exe");
    if (dat.empty()) return;
    try { core::cmd_init(game, dat); }
    catch (const std::exception& e) { MessageBoxA(owner, e.what(), "init failed", MB_ICONERROR); return; }
    set_status();
}

static void do_add(HWND owner) {
    std::string mod = pick_file(owner, L"Select .mgsv mod", L"SnakeBite mods", L"*.mgsv");
    if (mod.empty()) return;
    try { core::add_mod(mod); refresh_list(); }
    catch (const std::exception& e) { MessageBoxA(owner, e.what(), "add failed", MB_ICONERROR); }
}

static void do_remove(HWND owner) {
    int sel = selected_index();
    if (sel < 0 || sel >= (int)core::g_state.mods.size()) return;
    std::string id = core::g_state.mods[sel].id;
    if (MessageBoxA(owner, ("Remove mod " + id + "? (Run Apply afterwards to rebuild without it.)").c_str(),
                    "confirm", MB_YESNO | MB_ICONQUESTION) != IDYES) return;
    try { core::remove_mod(id); refresh_list(); }
    catch (const std::exception& e) { MessageBoxA(owner, e.what(), "remove failed", MB_ICONERROR); }
}

static void do_move(int delta) {
    int sel = selected_index();
    if (sel < 0 || sel >= (int)core::g_state.mods.size()) return;
    std::string id = core::g_state.mods[sel].id;
    core::move_mod(id, delta);
    refresh_list();
    int target = sel + delta;
    if (target >= 0 && target < (int)core::g_state.mods.size())
        ListView_SetItemState(g_hList, target, LVIS_SELECTED | LVIS_FOCUSED,
                              LVIS_SELECTED | LVIS_FOCUSED);
}

static void worker_apply() {
    g_busy = true;
    core::set_logger(gui_log);
    try { core::apply_all(); }
    catch (const std::exception& e) { gui_log(std::string("ERROR: ") + e.what()); }
    g_busy = false;
}

static void worker_revert() {
    g_busy = true;
    core::set_logger(gui_log);
    try { core::revert_all(); }
    catch (const std::exception& e) { gui_log(std::string("ERROR: ") + e.what()); }
    g_busy = false;
}

static void do_apply()  { if (g_busy) return; std::thread(worker_apply).detach(); }
static void do_revert(HWND owner) {
    if (g_busy) return;
    if (MessageBoxA(owner, "Revert all modded files to their pristine baselines?", "confirm",
                    MB_YESNO | MB_ICONQUESTION) != IDYES) return;
    std::thread(worker_revert).detach();
}

// ---- owner-draw flat button ----

static LRESULT CALLBACK BtnProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp, UINT_PTR, DWORD_PTR) {
    switch (msg) {
    case WM_MOUSEMOVE: {
        if (g_hHover != hwnd) {
            HWND prev = g_hHover; g_hHover = hwnd;
            if (prev) InvalidateRect(prev, nullptr, TRUE);
            InvalidateRect(hwnd, nullptr, TRUE);
            TRACKMOUSEEVENT t{ sizeof(t), TME_LEAVE, hwnd, 0 };
            TrackMouseEvent(&t);
        }
        break;
    }
    case WM_MOUSELEAVE:
        if (g_hHover == hwnd) { g_hHover = nullptr; InvalidateRect(hwnd, nullptr, TRUE); }
        break;
    case WM_LBUTTONDOWN:
        g_hPress = hwnd; InvalidateRect(hwnd, nullptr, TRUE); break;
    case WM_LBUTTONUP:
        if (g_hPress == hwnd) { g_hPress = nullptr; InvalidateRect(hwnd, nullptr, TRUE); }
        break;
    case WM_NCDESTROY:
        if (g_hHover == hwnd) g_hHover = nullptr;
        if (g_hPress == hwnd) g_hPress = nullptr;
        RemoveWindowSubclass(hwnd, BtnProc, 0);
        break;
    }
    return DefSubclassProc(hwnd, msg, wp, lp);
}

static void draw_button(LPDRAWITEMSTRUCT di) {
    RECT r = di->rcItem;
    bool hover = (g_hHover == di->hwndItem);
    bool down  = (g_hPress == di->hwndItem) && hover;
    bool focus = (di->itemState & ODS_FOCUS) != 0;

    COLORREF fill = down ? col::btn_down : (hover ? col::btn_hover : col::btn);
    HBRUSH bg = CreateSolidBrush(fill);
    FillRect(di->hDC, &r, bg);
    DeleteObject(bg);

    // 1px border
    HPEN pen = CreatePen(PS_SOLID, 1, col::border);
    HPEN op  = (HPEN)SelectObject(di->hDC, pen);
    HBRUSH ob = (HBRUSH)SelectObject(di->hDC, GetStockObject(NULL_BRUSH));
    Rectangle(di->hDC, r.left, r.top, r.right, r.bottom);
    SelectObject(di->hDC, op); SelectObject(di->hDC, ob);
    DeleteObject(pen);

    // text
    char txt[128]; GetWindowTextA(di->hwndItem, txt, sizeof(txt));
    SetBkMode(di->hDC, TRANSPARENT);
    SetTextColor(di->hDC, col::text);
    HFONT of = (HFONT)SelectObject(di->hDC, g_fUi);
    DrawTextA(di->hDC, txt, -1, &r, DT_CENTER | DT_VCENTER | DT_SINGLELINE);
    SelectObject(di->hDC, of);

    if (focus) {
        RECT fr = r; InflateRect(&fr, -3, -3);
        DrawFocusRect(di->hDC, &fr);
    }
}

// ---- ListView custom draw ----

static LRESULT lv_customdraw(LPNMLVCUSTOMDRAW nm) {
    switch (nm->nmcd.dwDrawStage) {
    case CDDS_PREPAINT:        return CDRF_NOTIFYITEMDRAW;
    case CDDS_ITEMPREPAINT: {
        int row = (int)nm->nmcd.dwItemSpec;
        bool selected = ListView_GetItemState(g_hList, row, LVIS_SELECTED) & LVIS_SELECTED;
        if (selected) nm->clrTextBk = col::sel;
        else          nm->clrTextBk = (row & 1) ? col::panel_alt : col::panel;
        nm->clrText = col::text;
        return CDRF_DODEFAULT;
    }
    }
    return CDRF_DODEFAULT;
}

// custom-drawn header so column titles don't blast white on black
static LRESULT hdr_customdraw(LPNMCUSTOMDRAW nm) {
    switch (nm->dwDrawStage) {
    case CDDS_PREPAINT:    return CDRF_NOTIFYITEMDRAW;
    case CDDS_ITEMPREPAINT: {
        HDC dc = nm->hdc;
        RECT r = nm->rc;
        HBRUSH bg = CreateSolidBrush(col::panel_alt);
        FillRect(dc, &r, bg);
        DeleteObject(bg);
        // bottom border
        RECT b = { r.left, r.bottom - 1, r.right, r.bottom };
        HBRUSH bd = CreateSolidBrush(col::border);
        FillRect(dc, &b, bd);
        DeleteObject(bd);
        // text
        HWND hHdr = ListView_GetHeader(g_hList);
        HDITEMA hi{}; char buf[128] = {0};
        hi.mask = HDI_TEXT; hi.pszText = buf; hi.cchTextMax = sizeof(buf);
        Header_GetItem(hHdr, (int)nm->dwItemSpec, &hi);
        RECT tr = r; tr.left += 8;
        SetBkMode(dc, TRANSPARENT);
        SetTextColor(dc, col::text_dim);
        HFONT of = (HFONT)SelectObject(dc, g_fUiBold);
        DrawTextA(dc, buf, -1, &tr, DT_LEFT | DT_VCENTER | DT_SINGLELINE);
        SelectObject(dc, of);
        return CDRF_SKIPDEFAULT;
    }
    }
    return CDRF_DODEFAULT;
}

// ---- window proc ----

static LRESULT CALLBACK WndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) {
    switch (msg) {
    case WM_CREATE: {
        apply_immersive_dark(hwnd);

        const int btn_y = 10, btn_h = 30, btn_w = 100, gap = 6, left = 10;
        struct Spec { int id; const char* txt; };
        Spec specs[] = {
            { IDC_BTN_ADD,    "+  Add" },
            { IDC_BTN_REMOVE, "-  Remove" },
            { IDC_BTN_UP,     "Up" },
            { IDC_BTN_DOWN,   "Down" },
            { IDC_BTN_APPLY,  "Apply" },
            { IDC_BTN_REVERT, "Revert" },
            { IDC_BTN_SETUP,  "Settings" },
        };
        for (int i = 0; i < (int)(sizeof(specs)/sizeof(specs[0])); ++i) {
            HWND b = CreateWindowExA(0, "BUTTON", specs[i].txt,
                WS_CHILD | WS_VISIBLE | BS_OWNERDRAW,
                left + i*(btn_w+gap), btn_y, btn_w, btn_h,
                hwnd, (HMENU)(INT_PTR)specs[i].id, nullptr, nullptr);
            SendMessage(b, WM_SETFONT, (WPARAM)g_fUi, TRUE);
            SetWindowSubclass(b, BtnProc, 0, 0);
        }

        g_hList = CreateWindowExA(0, WC_LISTVIEW, "",
            WS_CHILD | WS_VISIBLE | LVS_REPORT | LVS_SINGLESEL | LVS_SHOWSELALWAYS | LVS_OWNERDATA*0,
            10, 50, 100, 100, hwnd, (HMENU)IDC_LIST, nullptr, nullptr);
        ListView_SetExtendedListViewStyle(g_hList,
            LVS_EX_FULLROWSELECT | LVS_EX_DOUBLEBUFFER | LVS_EX_CHECKBOXES);
        SetWindowTheme(g_hList, L"DarkMode_Explorer", nullptr);
        ListView_SetBkColor(g_hList, col::panel);
        ListView_SetTextBkColor(g_hList, col::panel);
        ListView_SetTextColor(g_hList, col::text);
        SendMessage(g_hList, WM_SETFONT, (WPARAM)g_fUi, TRUE);

        auto addcol = [&](int i, const char* txt, int w) {
            LVCOLUMNA c{}; c.mask = LVCF_TEXT | LVCF_WIDTH;
            c.pszText = (LPSTR)txt; c.cx = w;
            ListView_InsertColumn(g_hList, i, &c);
        };
        addcol(0, "ID",      150);
        addcol(1, "Name",    280);
        addcol(2, "Version",  80);
        addcol(3, "Author",  150);
        addcol(4, "QAR",      60);
        addcol(5, "GameDir",  80);

        g_hLog = CreateWindowExA(0, "EDIT", "",
            WS_CHILD | WS_VISIBLE | WS_VSCROLL | ES_MULTILINE | ES_READONLY | ES_AUTOVSCROLL,
            10, 0, 100, 100, hwnd, (HMENU)IDC_LOG, nullptr, nullptr);
        SendMessage(g_hLog, WM_SETFONT, (WPARAM)g_fMono, TRUE);
        SetWindowTheme(g_hLog, L"DarkMode_Explorer", nullptr);

        g_hStatus = CreateWindowExA(0, STATUSCLASSNAMEA, "",
            WS_CHILD | WS_VISIBLE | SBT_NOBORDERS,
            0, 0, 0, 0, hwnd, (HMENU)IDC_STATUS, nullptr, nullptr);
        SendMessage(g_hStatus, WM_SETFONT, (WPARAM)g_fUi, TRUE);
        return 0;
    }
    case WM_SIZE: {
        RECT rc; GetClientRect(hwnd, &rc);
        SendMessage(g_hStatus, WM_SIZE, 0, 0);
        RECT sr; GetWindowRect(g_hStatus, &sr);
        int sh = sr.bottom - sr.top;
        int top    = 50;
        int bottom = rc.bottom - sh;
        int list_h = (bottom - top) * 65 / 100;
        int log_y  = top + list_h + 8;
        int log_h  = bottom - log_y - 8;
        MoveWindow(g_hList, 10, top,   rc.right - 20, list_h, TRUE);
        MoveWindow(g_hLog,  10, log_y, rc.right - 20, log_h,  TRUE);
        return 0;
    }
    case WM_ERASEBKGND: {
        HDC dc = (HDC)wp; RECT rc; GetClientRect(hwnd, &rc);
        FillRect(dc, &rc, g_brBg);
        // toolbar separator
        RECT sep = { 0, 47, rc.right, 48 };
        FillRect(dc, &sep, g_brBorder);
        return 1;
    }
    case WM_CTLCOLOREDIT:
    case WM_CTLCOLORSTATIC: {
        HDC dc = (HDC)wp;
        SetBkColor(dc,   col::panel);
        SetTextColor(dc, col::text);
        return (LRESULT)g_brPanel;
    }
    case WM_CTLCOLORBTN:
        return (LRESULT)g_brBg;
    case WM_DRAWITEM: {
        auto di = (LPDRAWITEMSTRUCT)lp;
        if (di->CtlType == ODT_BUTTON) { draw_button(di); return TRUE; }
        return FALSE;
    }
    case WM_NOTIFY: {
        auto nm = (LPNMHDR)lp;
        if (nm->idFrom == IDC_LIST) {
            if (nm->code == NM_CUSTOMDRAW) {
                LRESULT r = lv_customdraw((LPNMLVCUSTOMDRAW)lp);
                SetWindowLongPtr(hwnd, DWLP_MSGRESULT, r);
                return TRUE;
            }
            if (nm->code == LVN_ITEMCHANGED) {
                auto p = (LPNMLISTVIEW)lp;
                if (p->uChanged & LVIF_STATE) {
                    UINT before = (p->uOldState & LVIS_STATEIMAGEMASK) >> 12;
                    UINT after  = (p->uNewState & LVIS_STATEIMAGEMASK) >> 12;
                    if (before && after && before != after &&
                        p->iItem >= 0 && p->iItem < (int)core::g_state.mods.size()) {
                        bool checked = (after == 2);
                        try { core::enable_mod(core::g_state.mods[p->iItem].id, checked); }
                        catch (const std::exception& e) { append_log(std::string("ERROR: ") + e.what()); }
                    }
                }
            }
        }
        // header custom-draw
        if (nm->code == NM_CUSTOMDRAW &&
            nm->hwndFrom == ListView_GetHeader(g_hList)) {
            LRESULT r = hdr_customdraw((LPNMCUSTOMDRAW)lp);
            SetWindowLongPtr(hwnd, DWLP_MSGRESULT, r);
            return TRUE;
        }
        return 0;
    }
    case WM_COMMAND: {
        switch (LOWORD(wp)) {
        case IDC_BTN_ADD:    do_add(hwnd);      break;
        case IDC_BTN_REMOVE: do_remove(hwnd);   break;
        case IDC_BTN_UP:     do_move(-1);       break;
        case IDC_BTN_DOWN:   do_move(+1);       break;
        case IDC_BTN_APPLY:  do_apply();        break;
        case IDC_BTN_REVERT: do_revert(hwnd);   break;
        case IDC_BTN_SETUP:  do_settings(hwnd); break;
        }
        return 0;
    }
    case WM_APP_LOG: {
        HLOCAL h = (HLOCAL)wp;
        const char* p = (const char*)h;
        append_log(p);
        LocalFree(h);
        return 0;
    }
    case WM_CLOSE: DestroyWindow(hwnd); return 0;
    case WM_DESTROY: PostQuitMessage(0); return 0;
    }
    return DefWindowProcA(hwnd, msg, wp, lp);
}

int APIENTRY WinMain(HINSTANCE hInst, HINSTANCE, LPSTR, int nShow) {
    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);

    INITCOMMONCONTROLSEX icc{ sizeof(icc), ICC_LISTVIEW_CLASSES | ICC_BAR_CLASSES | ICC_STANDARD_CLASSES };
    InitCommonControlsEx(&icc);

    g_brBg     = CreateSolidBrush(col::bg);
    g_brPanel  = CreateSolidBrush(col::panel);
    g_brBtn    = CreateSolidBrush(col::btn);
    g_brBorder = CreateSolidBrush(col::border);

    g_fUi = CreateFontA(-15, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                       OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                       VARIABLE_PITCH, "Segoe UI");
    g_fUiBold = CreateFontA(-14, 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                       OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                       VARIABLE_PITCH, "Segoe UI");
    g_fMono = CreateFontA(-14, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                       OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                       FIXED_PITCH | FF_MODERN, "Consolas");

    core::init_paths();
    core::load_state();

    WNDCLASSA wc{};
    wc.lpfnWndProc   = WndProc;
    wc.hInstance     = hInst;
    wc.hCursor       = LoadCursor(nullptr, IDC_ARROW);
    wc.hbrBackground = g_brBg;
    wc.lpszClassName = "mgsv_modmgr_main";
    RegisterClassA(&wc);

    g_hMain = CreateWindowExA(0, "mgsv_modmgr_main", "mgsv_modmgr",
                              WS_OVERLAPPEDWINDOW, CW_USEDEFAULT, CW_USEDEFAULT,
                              1080, 740, nullptr, nullptr, hInst, nullptr);
    ShowWindow(g_hMain, nShow);
    UpdateWindow(g_hMain);

    refresh_list();
    set_status();

    if (core::g_state.game_root.empty() || core::g_state.datfpk.empty())
        append_log("Not initialised yet. Click Settings to point at the game root and datfpk.exe.");

    MSG msg;
    while (GetMessage(&msg, nullptr, 0, 0)) {
        TranslateMessage(&msg);
        DispatchMessage(&msg);
    }

    DeleteObject(g_brBg); DeleteObject(g_brPanel); DeleteObject(g_brBtn); DeleteObject(g_brBorder);
    DeleteObject(g_fUi);  DeleteObject(g_fUiBold); DeleteObject(g_fMono);
    CoUninitialize();
    return (int)msg.wParam;
}
