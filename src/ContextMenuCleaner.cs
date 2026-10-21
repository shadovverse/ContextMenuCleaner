// ContextMenuCleaner — скрывает выбранные пункты классического контекстного меню Проводника.
// Ничего не удаляет и не трогает HKLM: всё пишется в HKCU, поставленная обратно галочка возвращает пункт.
//   команда (shell\<verb>)   -> значение ProgrammaticAccessOnly в HKCU\Software\Classes\...
//   расширение (CLSID)       -> значение в HKCU\...\Shell Extensions\Blocked
// Список берётся не из реестра как есть, а у самой оболочки: для образцов (файл, программа,
// ярлык, папка, диск, фон папки, рабочий стол) строится настоящее меню, и остаются только
// пункты, которые в нём реально появляются, — с теми же подписями, что видит пользователь.
// Сборка: src\build.cmd, exe кладётся уровнем выше (csc из .NET Framework 4, есть в любой Windows 10/11). Язык — C# 5.
// Проверка: ContextMenuCleaner.exe --dump out.txt — пишет найденный список в файл.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Xml;
using Microsoft.Win32;

namespace Cmc {

static class L {
    public static readonly bool Ru = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru";
    public static string T(string ru, string en) { return Ru ? ru : en; }
}

static class Dpi {
    public static float K = 1;
    public static int S(float v) { return (int)Math.Round(v * K); }
}

static class Native {
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] public static extern int SHLoadIndirectString(string src, StringBuilder buf, int size, IntPtr reserved);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] public static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr w, string l);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromDC(IntPtr dc);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SHParseDisplayName(string name, IntPtr bc, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);
    [DllImport("shell32.dll")] public static extern int SHGetDesktopFolder(out IntPtr ppshf);
    [DllImport("shell32.dll")] public static extern int SHCreateShellItemArrayFromDataObject(IntPtr pdo, ref Guid riid, out IntPtr ppv);
    [DllImport("shell32.dll")] public static extern int SHCreateShellItemArrayFromIDLists(uint cidl, IntPtr[] pidls, out IntPtr ppv);
    [DllImport("shell32.dll")] public static extern IntPtr ILClone(IntPtr pidl);
    [DllImport("shell32.dll")] public static extern bool ILRemoveLastID(IntPtr pidl);
    [DllImport("shell32.dll")] public static extern IntPtr ILFindLastID(IntPtr pidl);
    [DllImport("ole32.dll")] public static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint ctx, ref Guid iid, out IntPtr ppv);
    [DllImport("user32.dll")] public static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] public static extern bool DestroyMenu(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetMenuItemCount(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetMenuItemID(IntPtr h, int pos);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetMenuString(IntPtr h, uint item, StringBuilder s, int max, uint flags);
}

public struct RECT { public int Left, Top, Right, Bottom; }

// NMHDR — отдельной структурой: на x64 она добивается до 24 байт, и stage идёт после добивки.
[StructLayout(LayoutKind.Sequential)]
struct NMHDR { public IntPtr hwndFrom, idFrom; public int code; }

[StructLayout(LayoutKind.Sequential)]
struct NMCUSTOMDRAW {
    public NMHDR hdr;
    public int stage;
    public IntPtr hdc;
    public RECT rc;
    public IntPtr item;
    public uint state;
    public IntPtr param;
}

// Порядок методов — это vtable, менять нельзя. Методы-заглушки не вызываются, они держат места.
[ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IShellFolder {
    void ParseDisplayName();
    void EnumObjects();
    [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
    void BindToStorage();
    void CompareIDs();
    [PreserveSig] int CreateViewObject(IntPtr hwnd, ref Guid riid, out IntPtr ppv);
    void GetAttributesOf();
    [PreserveSig] int GetUIObjectOf(IntPtr hwnd, uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref Guid riid, IntPtr reserved, out IntPtr ppv);
}

[ComImport, Guid("000214E8-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IShellExtInit {
    [PreserveSig] int Initialize(IntPtr pidlFolder, IntPtr dataObject, IntPtr hkeyProgId);
}

[ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IContextMenu {
    [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint index, uint idFirst, uint idLast, uint flags);
    [PreserveSig] int InvokeCommand(IntPtr info);
    [PreserveSig] int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, IntPtr name, uint cch);
}

[ComImport, Guid("A08CE4D0-FA25-44AB-B57C-C7B1C323E0B9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IExplorerCommand {
    [PreserveSig] int GetTitle(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] out string title);
    [PreserveSig] int GetIcon(IntPtr items, out IntPtr icon);
    [PreserveSig] int GetToolTip(IntPtr items, out IntPtr tip);
    [PreserveSig] int GetCanonicalName(out Guid guid);
    [PreserveSig] int GetState(IntPtr items, [MarshalAs(UnmanagedType.Bool)] bool okToBeSlow, out uint state);
}

// Образец объекта, по которому щёлкают правой кнопкой.
class Sample {
    public string Label;
    public IntPtr Folder, Data, Items;   // pidl папки, IDataObject выделения, IShellItemArray
    public readonly Dictionary<string, string> Verbs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // глагол -> подпись в меню
    public readonly HashSet<string> Texts = new HashSet<string>();   // все подписи полного меню, включая подменю-пункты
}

static class Probe {
    static Guid IID_IShellFolder = new Guid("000214E6-0000-0000-C000-000000000046");
    static Guid IID_IContextMenu = new Guid("000214E4-0000-0000-C000-000000000046");
    static Guid IID_IDataObject = new Guid("0000010E-0000-0000-C000-000000000046");
    static Guid IID_IShellItemArray = new Guid("B63EA76D-1F85-456F-A19C-48159EFA858B");
    static Guid IID_IUnknown = new Guid("00000000-0000-0000-C000-000000000046");
    const uint CMF_EXPLORE = 4;
    [ThreadStatic] static IShellFolder desktop;   // COM-объект годен только в своём потоке

    // Выделенный файл, папка или диск. menu — собрать ещё и полное меню (дорого: грузит все расширения).
    public static Sample Item(string path, string label, bool menu = false) {
        IntPtr pidl; uint a;
        if (path == null || Native.SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out a) != 0) return null;
        var s = new Sample { Label = label, Folder = Native.ILClone(pidl) };
        Native.ILRemoveLastID(s.Folder);
        var parent = Bind(s.Folder);
        if (parent == null) return null;
        var child = new[] { Native.ILFindLastID(pidl) };
        IntPtr cm;
        parent.GetUIObjectOf(IntPtr.Zero, 1, child, ref IID_IDataObject, IntPtr.Zero, out s.Data);
        if (s.Data != IntPtr.Zero) Native.SHCreateShellItemArrayFromDataObject(s.Data, ref IID_IShellItemArray, out s.Items);
        if (menu && parent.GetUIObjectOf(IntPtr.Zero, 1, child, ref IID_IContextMenu, IntPtr.Zero, out cm) == 0) Collect(cm, s);
        return s;
    }

    // Пустое место в папке; path == null — рабочий стол.
    public static Sample Background(string path, string label, bool menu) {
        IntPtr pidl; uint a;
        if (path == null) { pidl = Marshal.AllocCoTaskMem(2); Marshal.WriteInt16(pidl, 0); }
        else if (Native.SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out a) != 0) return null;
        var s = new Sample { Label = label, Folder = pidl };
        Native.SHCreateShellItemArrayFromIDLists(1, new[] { pidl }, out s.Items);
        var f = Bind(pidl);
        IntPtr cm;
        if (menu && f != null && f.CreateViewObject(IntPtr.Zero, ref IID_IContextMenu, out cm) == 0) Collect(cm, s);
        return s;
    }

    static IShellFolder Bind(IntPtr pidl) {
        if (desktop == null) {
            IntPtr d;
            Native.SHGetDesktopFolder(out d);
            desktop = (IShellFolder)Marshal.GetObjectForIUnknown(d);
            Marshal.Release(d);
        }
        if (Marshal.ReadInt16(pidl) == 0) return desktop;
        IntPtr p;
        if (desktop.BindToObject(pidl, IntPtr.Zero, ref IID_IShellFolder, out p) != 0) return null;
        var f = (IShellFolder)Marshal.GetObjectForIUnknown(p);
        Marshal.Release(p);
        return f;
    }

    // Полное меню образца: по глаголам видно, какие команды реестра в нём реально есть.
    static void Collect(IntPtr ptr, Sample s) {
        var cm = (IContextMenu)Marshal.GetObjectForIUnknown(ptr);
        Marshal.Release(ptr);
        IntPtr h = Native.CreatePopupMenu();
        try {
            if (cm.QueryContextMenu(h, 0, 1, 0x7FFF, CMF_EXPLORE) < 0) return;
            for (int i = 0; i < Native.GetMenuItemCount(h); i++) {
                s.Texts.Add(Text(h, i));
                uint id = Native.GetMenuItemID(h, i);
                if (id == 0 || id == uint.MaxValue) continue;
                string verb = VerbOf(cm, id - 1);
                if (!string.IsNullOrEmpty(verb) && !s.Verbs.ContainsKey(verb)) s.Verbs[verb] = Text(h, i);
            }
        } catch (Exception) { }
        finally { Native.DestroyMenu(h); Marshal.ReleaseComObject(cm); }
    }

    static string VerbOf(IContextMenu cm, uint offset) {
        IntPtr buf = Marshal.AllocHGlobal(1024);
        try {
            Marshal.WriteInt16(buf, 0);
            return cm.GetCommandString((UIntPtr)offset, 4 /* GCS_VERBW */, IntPtr.Zero, buf, 512) == 0 ? Marshal.PtrToStringUni(buf) : null;
        } catch (Exception) { return null; }
        finally { Marshal.FreeHGlobal(buf); }
    }

    static string Text(IntPtr h, int pos) {
        var sb = new StringBuilder(256);
        Native.GetMenuString(h, (uint)pos, sb, sb.Capacity, 0x400 /* MF_BYPOSITION */);
        string t = sb.ToString();
        int tab = t.IndexOf('\t');
        if (tab >= 0) t = t.Substring(0, tab);
        return t.Replace("&&", "\u0001").Replace("&", "").Replace("\u0001", "&").Trim();
    }

    static object Create(string clsid) {
        Guid c;
        try { c = new Guid(clsid); } catch (Exception) { return null; }
        IntPtr p;
        if (Native.CoCreateInstance(ref c, IntPtr.Zero, 1 | 4 /* INPROC | LOCAL */, ref IID_IUnknown, out p) != 0) return null;
        var o = Marshal.GetObjectForIUnknown(p);
        Marshal.Release(p);
        return o;
    }

    // Расширение создаётся напрямую (минуя список Blocked) и само показывает, что добавит в меню.
    public static string Handler(string clsid, string cls, Sample s) {
        object o = Create(clsid);
        if (o == null) return null;
        IntPtr h = Native.CreatePopupMenu();
        try {
            var init = o as IShellExtInit;
            if (init != null)
                using (var k = Registry.ClassesRoot.OpenSubKey(cls))
                    if (init.Initialize(s.Folder, s.Data, k == null ? IntPtr.Zero : k.Handle.DangerousGetHandle()) < 0) return null;
            var cm = o as IContextMenu;
            if (cm == null || cm.QueryContextMenu(h, 0, 1, 0x7FFF, CMF_EXPLORE) < 0) return null;
            var texts = new List<string>();
            for (int i = 0; i < Native.GetMenuItemCount(h); i++) {
                string t = Text(h, i);
                if (t.Length > 0 && !texts.Contains(t)) texts.Add(t);
            }
            return texts.Count > 0 ? string.Join(" / ", texts) : null;
        } catch (Exception) { return null; }
        finally { Native.DestroyMenu(h); Marshal.ReleaseComObject(o); }
    }

    // Команда приложения из Store (IExplorerCommand): сама говорит, видна ли она и как называется.
    // Создаётся один раз на все образцы — запуск её COM-сервера стоит ~100 мс.
    // Результат: образец -> подпись, только там, где команда видна.
    public static Dictionary<Sample, string> Command(string clsid, IEnumerable<Sample> samples) {
        var found = new Dictionary<Sample, string>();
        object o = Create(clsid);
        var ec = o as IExplorerCommand;
        if (ec == null) return found;
        foreach (var s in samples) {
            try {
                uint state; string title;
                if (ec.GetState(s.Items, true, out state) < 0 || (state & 2 /* ECS_HIDDEN */) != 0) continue;
                if (ec.GetTitle(s.Items, out title) == 0 && !string.IsNullOrEmpty(title)) found[s] = title.Replace("&", "").Trim();
            } catch (Exception) { }
        }
        Marshal.ReleaseComObject(o);
        return found;
    }
}

// Одно значение реестра, которое прячет пункт.
class Target {
    public string Sub, Value;
    public bool Hidden;
}

// Строка списка. Одинаковые подписи (например, «Закрепить на панели быстрого доступа» у папок
// и у дисков — это разные ключи) сливаются в одну строку, которая управляет всеми ключами сразу.
class Item {
    public string Name, Program;
    public readonly List<string> Where = new List<string>();
    public readonly List<Target> Targets = new List<Target>();
    public bool Hidden { get { return Targets.All(t => t.Hidden); } }
}

static class Scan {
    const string Blocked = @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";
    // Сам ярлык (CLSID_ShellLink) — его блокировка ломает все ярлыки.
    const string Never = "{00021401-0000-0000-C000-000000000046}";

    // Эти расширения Windows показывают себя только внутри самого Проводника, а при опросе
    // из другой программы молчат — их подписи и места известны заранее.
    class Known { public string Ru, En; public bool ExplorerOnly, ProgramsOnly; }
    static readonly Dictionary<string, Known> Names = new Dictionary<string, Known>(StringComparer.OrdinalIgnoreCase) {
        { "{09A47860-11B0-4DA5-AFA5-26D86198A780}", new Known { Ru = "Проверка с помощью Microsoft Defender", En = "Scan with Microsoft Defender", ExplorerOnly = true } },
        { "{90AA3A4E-1CBA-4233-B8BB-535773D48449}", new Known { Ru = "Закрепить на панели задач", En = "Pin to taskbar", ExplorerOnly = true, ProgramsOnly = true } },
        { "{1D27F844-3A1F-4410-85AC-14651078412D}", new Known { Ru = "Исправление проблем с совместимостью", En = "Troubleshoot compatibility", ExplorerOnly = true, ProgramsOnly = true } },
        // уже скрытые пользователем и не отвечающие на опрос — только подпись
        { "{E2BF9676-5F8F-435C-97EB-11607A5BEDF7}", new Known { Ru = "Поделиться", En = "Share" } },
    };

    static Dictionary<string, Item> items;
    static string dir;

    public static List<Item> Run() {
        items = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
        dir = Path.Combine(Path.GetTempPath(), "ContextMenuCleaner");
        try {
            Directory.CreateDirectory(Path.Combine(dir, "folder"));
            Touch("sample.txt");
            // Команды Store-приложений опрашиваются параллельно: каждая поднимает свой COM-сервер
            // (~100 мс), а полные меню образцов им не нужны. COM-объекты образцов привязаны
            // к своему потоку, поэтому у каждого потока свой набор.
            var pk = new Thread(() => { try { Packages(Samples(false)); } catch (Exception) { } });
            pk.SetApartmentState(ApartmentState.STA);
            pk.Start();
            foreach (var kv in Samples(true)) { Verbs(kv.Key, kv.Value); Handlers(kv.Key, kv.Value); }
            pk.Join();
        } finally {
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
        var cmp = StringComparer.Create(CultureInfo.CurrentCulture, true);
        return items.Values.OrderBy(i => i.Name, cmp).ToList();
    }

    // Образцы и какие классы реестра к ним относятся.
    static Dictionary<string, Sample[]> Samples(bool menus) {
        var txt = Probe.Item(Path.Combine(dir, "sample.txt"), L.T("файлы", "files"), menus);
        var exe = Probe.Item(Path.Combine(Environment.SystemDirectory, "cmd.exe"), L.T("программы", "programs"), menus);
        var lnk = Probe.Item(AnyShortcut(), L.T("ярлыки", "shortcuts"), menus);
        var folder = Probe.Item(Path.Combine(dir, "folder"), L.T("папки", "folders"), menus);
        var drive = Probe.Item(Path.GetPathRoot(Environment.SystemDirectory), L.T("диски", "drives"), menus);
        var bg = Probe.Background(dir, L.T("фон папки", "folder background"), menus);
        var desk = Probe.Background(null, L.T("рабочий стол", "desktop"), menus);
        var byClass = new Dictionary<string, Sample[]>(StringComparer.OrdinalIgnoreCase) {
            { "*", new[] { txt, exe, lnk } }, { "AllFilesystemObjects", new[] { txt, exe, lnk, folder } },
            { "exefile", new[] { exe } }, { "lnkfile", new[] { lnk } },
            { "Directory", new[] { folder } }, { "Folder", new[] { folder, drive } }, { "Drive", new[] { drive } },
            { "Directory\\Background", new[] { bg } }, { "DesktopBackground", new[] { desk } },
        };
        foreach (var cls in byClass.Keys.ToList()) byClass[cls] = byClass[cls].Where(s => s != null).ToArray();
        return byClass;
    }

    static string Touch(string name) {
        string p = Path.Combine(dir, name);
        File.WriteAllBytes(p, new byte[0]);
        return p;
    }

    static string AnyShortcut() {
        foreach (var root in new[] { Environment.SpecialFolder.CommonPrograms, Environment.SpecialFolder.Programs }) {
            try {
                var f = Directory.EnumerateFiles(Environment.GetFolderPath(root), "*.lnk", SearchOption.AllDirectories).FirstOrDefault();
                if (f != null) return f;
            } catch (Exception) { }
        }
        return null;
    }

    // Расширение может быть заблокировано и для всей машины (HKLM) — например, другой
    // программой-чистильщиком. Опрос создаёт расширение в обход блокировки, так что без этой
    // проверки такой пункт выглядел бы видимым.
    public static bool HasValue(string sub, string value) {
        if (sub == Blocked && MachineBlocked(value)) return true;
        using (var k = Registry.CurrentUser.OpenSubKey(sub)) return k != null && k.GetValue(value) != null;
    }

    // Заблокированную команду приложения Windows не даёт создать, спросить подпись не у кого.
    // «Применить» пишет подпись в данные записи блокировки (Windows смотрит только на имя значения).
    // Только HKCU: в HKLM данные пишут чужие программы, и там бывает мусор (видели «/f»).
    static string SavedName(string clsid) {
        using (var k = Registry.CurrentUser.OpenSubKey(Blocked)) {
            var s = k == null ? null : k.GetValue(clsid) as string;
            return string.IsNullOrEmpty(s) ? null : s;
        }
    }

    public static bool MachineBlocked(string clsid) {
        using (var k = Registry.LocalMachine.OpenSubKey(Blocked)) return k != null && k.GetValue(clsid) != null;
    }

    // Снять блокировку для всей машины; без прав администратора — false.
    public static bool MachineUnblock(string clsid) {
        try {
            using (var k = Registry.LocalMachine.OpenSubKey(Blocked, true)) k.DeleteValue(clsid, false);
            return true;
        } catch (Exception) { return false; }
    }

    static void Add(string name, IEnumerable<string> where, string program, string sub, string value, bool hidden) {
        lock (items) {   // зовётся из двух потоков (см. Run)
            Item it;
            if (!items.TryGetValue(name, out it)) items[name] = it = new Item { Name = name, Program = program ?? "" };
            foreach (var w in where) if (!it.Where.Contains(w)) it.Where.Add(w);
            if (!it.Targets.Any(t => t.Sub == sub && t.Value == value)) it.Targets.Add(new Target { Sub = sub, Value = value, Hidden = hidden });
        }
    }

    static void Verbs(string cls, Sample[] samples) {
        using (var shell = Registry.ClassesRoot.OpenSubKey(cls + "\\shell")) {
            if (shell == null) return;
            foreach (var verb in shell.GetSubKeyNames()) {
                if (verb == "open" || verb == "explore") continue;
                string sub = "Software\\Classes\\" + cls + "\\shell\\" + verb, name = null;
                bool hidden = HasValue(sub, "ProgrammaticAccessOnly");
                var where = new List<string>();
                foreach (var s in samples) {
                    string t;
                    if (s.Verbs.TryGetValue(verb, out t)) { where.Add(s.Label); if (string.IsNullOrEmpty(name)) name = t; }
                }
                if (where.Count == 0 && !hidden) continue;   // в меню не появляется
                using (var k = shell.OpenSubKey(verb)) {
                    if (k == null) continue;
                    if (string.IsNullOrEmpty(name)) name = Resolve(k.GetValue("MUIVerb") as string ?? k.GetValue("") as string) ?? verb;
                    Add(name, where.Count > 0 ? where : samples.Select(s => s.Label), VerbProgram(k), sub, "ProgrammaticAccessOnly", hidden);
                }
            }
        }
    }

    static void Handlers(string cls, Sample[] samples) {
        using (var hs = Registry.ClassesRoot.OpenSubKey(cls + "\\shellex\\ContextMenuHandlers")) {
            if (hs == null) return;
            foreach (var key in hs.GetSubKeyNames()) {
                string keyName = key.Trim(), id = keyName;
                if (!keyName.StartsWith("{"))
                    using (var k = hs.OpenSubKey(key)) id = k == null ? null : k.GetValue("") as string;
                Guid g;
                if (id == null || !Guid.TryParse(id, out g) || id.Equals(Never, StringComparison.OrdinalIgnoreCase)) continue;
                id = g.ToString("B").ToUpperInvariant();
                bool hidden = HasValue(Blocked, id);
                string name = null;
                var where = new List<string>();
                foreach (var s in samples) {
                    // Опрошенное напрямую расширение иногда добавляет пункт и там, где Проводник
                    // его не покажет, — засчитываем только если подпись есть в полном меню образца.
                    string t = Probe.Handler(id, cls, s);
                    if (t == null) continue;
                    if (name == null) name = t;   // подпись нужна и скрытому пункту, которого в меню уже нет
                    if (t.Split(new[] { " / " }, StringSplitOptions.None).Any(s.Texts.Contains)) where.Add(s.Label);
                }
                Known kn;
                if (Names.TryGetValue(id, out kn)) {
                    if (name == null) name = L.T(kn.Ru, kn.En);
                    if (where.Count == 0 && kn.ExplorerOnly && ClsidProgram(id) != null)
                        where.AddRange(kn.ProgramsOnly ? new[] { L.T("программы", "programs"), L.T("ярлыки", "shortcuts") } : samples.Select(s => s.Label));
                }
                if (where.Count == 0 && !hidden) continue;
                if (name == null)
                    using (var c = Registry.ClassesRoot.OpenSubKey("CLSID\\" + id)) name = (c == null ? null : c.GetValue("") as string) ?? keyName;
                Add(name, where.Count > 0 ? where : samples.Select(s => s.Label), ClsidProgram(id), Blocked, id, hidden);
            }
        }
    }

    // Пункты UWP/MSIX-приложений (Блокнот, Терминал...) живут в манифестах пакетов, не в HKCR.
    // Папки пакетов пользователя — в репозитории AppModel (то же, что отдаёт Get-AppxPackage).
    static void Packages(Dictionary<string, Sample[]> byClass) {
        const string repo = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
        using (var r = Registry.CurrentUser.OpenSubKey(repo)) {
            if (r == null) return;
            foreach (var full in r.GetSubKeyNames()) {
                string root, display;
                using (var k = r.OpenSubKey(full)) {
                    root = k == null ? null : k.GetValue("PackageRootFolder") as string;
                    display = k == null ? null : k.GetValue("DisplayName") as string;
                }
                if (root == null) continue;
                // У «разреженных» пакетов (WinRAR, OneDrive) корень — папка программы,
                // а манифест лежит в WindowsApps под полным именем пакета.
                string manifest = Path.Combine(root, "AppxManifest.xml");
                if (!File.Exists(manifest))
                    manifest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps", full, "AppxManifest.xml");
                XmlDocument xml;
                try {
                    if (!File.Exists(manifest)) continue;
                    string text = File.ReadAllText(manifest);
                    if (!text.Contains("FileExplorerContextMenus")) continue;
                    xml = new XmlDocument();
                    xml.LoadXml(text);
                } catch (Exception) { continue; }   // битый или недоступный манифест — пропускаем пакет

                string program = PackageName(full, display);
                var types = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);   // CLSID -> типы
                foreach (XmlElement v in xml.SelectNodes("//*[local-name()='FileExplorerContextMenus']/*/*[local-name()='Verb']")) {
                    string id = ("{" + v.GetAttribute("Clsid") + "}").ToUpperInvariant();
                    if (!types.ContainsKey(id)) types[id] = new List<string>();
                    types[id].Add(((XmlElement)v.ParentNode).GetAttribute("Type"));
                }
                foreach (var kv in types) {
                    var samples = new List<Sample>();
                    foreach (var t in kv.Value) { Sample[] ss; if (byClass.TryGetValue(t, out ss)) samples.AddRange(ss); }
                    var exts = kv.Value.Where(t => t.StartsWith(".")).ToList();
                    if (exts.Count > 0) {
                        string label = string.Join(", ", exts.Take(3)) + (exts.Count > 3 ? ", …" : "");
                        var s = Probe.Item(Touch("sample" + exts[0]), label);
                        if (s != null) samples.Add(s);
                    }
                    bool hidden = HasValue(Blocked, kv.Key);
                    var found = Probe.Command(kv.Key, samples.Distinct());
                    if (found.Count == 0 && !hidden) continue;
                    var where = found.Count > 0 ? found.Keys.Select(s => s.Label) : samples.Select(s => s.Label).Distinct();
                    Add(found.Count > 0 ? found.Values.First() : SavedName(kv.Key) ?? program, where, program, Blocked, kv.Key, hidden);
                }
            }
        }
    }

    static string Resolve(string s) {
        if (string.IsNullOrEmpty(s)) return null;
        if (s.StartsWith("@")) {
            var sb = new StringBuilder(512);
            if (Native.SHLoadIndirectString(s, sb, sb.Capacity, IntPtr.Zero) == 0) s = sb.ToString();
        }
        return s.Replace("&", "").Trim();
    }

    // Имя пакета в репозитории — ссылка на ресурс: «ms-resource:AppStoreName»,
    // «ms-resource:Resources/AppStoreName» или уже готовое «@{...}».
    static string PackageName(string full, string display) {
        string pkg = full.Split('_')[0];
        if (string.IsNullOrEmpty(display)) return pkg;
        var uris = new List<string>();
        if (display.StartsWith("@")) uris.Add(display);
        else if (display.StartsWith("ms-resource:")) {
            string key = display.Substring(12).TrimStart('/');
            uris.Add("@{" + full + "?ms-resource://" + pkg + "/Resources/" + key + "}");
            uris.Add("@{" + full + "?ms-resource://" + pkg + "/" + key + "}");
        } else return display;
        foreach (var u in uris) {
            string s = Resolve(u);
            if (s != null && !s.StartsWith("@")) return s;
        }
        return pkg;
    }

    // «Программа» — чей это пункт: Windows, WinRAR, NVIDIA... по свойствам файла.
    static string FileProgram(string file) {
        if (string.IsNullOrEmpty(file)) return null;
        file = Environment.ExpandEnvironmentVariables(file).Trim();
        if (file.StartsWith("\"")) {
            int q = file.IndexOf('"', 1);
            file = q > 0 ? file.Substring(1, q - 1) : file.Trim('"');
        } else {
            int e = file.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (e > 0) file = file.Substring(0, e + 4);
        }
        try {
            if (!Path.IsPathRooted(file)) file = Path.Combine(Environment.SystemDirectory, file);
            if (!File.Exists(file)) return null;
            var v = FileVersionInfo.GetVersionInfo(file);
            string p = (v.ProductName ?? "").Replace("®", "").Replace("™", "").Replace("(R)", "").Trim();
            string c = (v.CompanyName ?? "").Trim();
            int dash = p.IndexOf(" - ");   // «BleachBit - Free space and maintain privacy»
            if (dash > 0) p = p.Substring(0, dash);
            if (p.Contains("Windows") || (p.Length == 0 && c.StartsWith("Microsoft"))) return "Windows";
            return p.Length > 0 ? p : c.Length > 0 ? c : Path.GetFileNameWithoutExtension(file);
        } catch (Exception) { return null; }
    }

    static string ClsidProgram(string id) {
        foreach (var server in new[] { "InprocServer32", "LocalServer32" })
            using (var k = Registry.ClassesRoot.OpenSubKey("CLSID\\" + id + "\\" + server)) {
                string p = FileProgram(k == null ? null : k.GetValue("") as string);
                if (p != null) return p;
            }
        return null;
    }

    static string VerbProgram(RegistryKey verb) {
        using (var cmd = verb.OpenSubKey("command")) {
            string p = FileProgram(cmd == null ? null : cmd.GetValue("") as string);
            if (p != null) return p;
            string handler = verb.GetValue("ExplorerCommandHandler") as string ?? (cmd == null ? null : cmd.GetValue("DelegateExecute") as string);
            return (handler == null ? null : ClsidProgram(handler)) ?? "Windows";
        }
    }
}

// «Классическое» меню Windows 11: пустой InprocServer32 у этого CLSID отключает новое меню,
// и сразу открывается полное, без «Показать дополнительные параметры».
static class Classic {
    const string Key = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";
    public static bool On {
        get { using (var k = Registry.CurrentUser.OpenSubKey(Key + "\\InprocServer32")) return k != null; }
        set {
            if (value) using (var k = Registry.CurrentUser.CreateSubKey(Key + "\\InprocServer32")) k.SetValue("", "");
            else Registry.CurrentUser.DeleteSubKeyTree(Key, false);
        }
    }
    public static bool Supported {
        get {
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion")) {
                int b;
                return k != null && int.TryParse(k.GetValue("CurrentBuildNumber") as string, out b) && b >= 22000;
            }
        }
    }
}

// Палитра Windows 11 (цвета WinUI: фон окна, карточка, обводка, текст) + акцент системы.
static class Pal {
    public static bool Dark;
    public static Color Bg, Card, Line, Text, Sub, Hover, Box, BoxFill, Accent, OnAccent, Btn, BtnLine, Field, FieldFocus;

    public static void Load() {
        using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")) {
            object v = k == null ? null : k.GetValue("AppsUseLightTheme");
            Dark = v is int && (int)v == 0;
        }
        // AccentPalette — 8 оттенков по 4 байта RGBA; в тёмной теме система берёт Light2, в светлой Dark1.
        Accent = Color.FromArgb(0, 120, 215);
        using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent")) {
            var p = k == null ? null : k.GetValue("AccentPalette") as byte[];
            int i = Dark ? 1 : 4;
            if (p != null && p.Length >= 32) Accent = Color.FromArgb(p[i * 4], p[i * 4 + 1], p[i * 4 + 2]);
        }
        if (Dark) {
            Bg = G(0x20); Card = G(0x2B); Line = G(0x1C); Text = Color.White; Sub = G(0xC5); Hover = G(0x37);
            Box = G(0x9E); BoxFill = G(0x27); OnAccent = Color.Black; Btn = G(0x37); BtnLine = G(0x45);
            Field = G(0x2D); FieldFocus = G(0x1F);
        } else {
            Bg = G(0xF3); Card = G(0xFB); Line = G(0xE5); Text = G(0x1B); Sub = G(0x5D); Hover = G(0xEE);
            Box = G(0x86); BoxFill = G(0xF5); OnAccent = Color.White; Btn = G(0xFD); BtnLine = G(0xD9);
            Field = G(0xFB); FieldFocus = Color.White;
        }
    }
    static Color G(int v) { return Color.FromArgb(v, v, v); }

    public static Color Mix(Color a, Color b, float t) {
        return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }

    public static GraphicsPath Round(RectangleF r, float rad) {
        var p = new GraphicsPath(); float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static readonly string IconFont = new System.Drawing.Text.InstalledFontCollection().Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
}

// Окно: заголовок в цвет фона, тёмные кнопки заголовка; само перекрашивается при смене темы.
class W11Form : Form {
    public W11Form() { Pal.Load(); BackColor = Pal.Bg; ForeColor = Pal.Text; }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Frame(); }

    void Frame() {
        int v = Pal.Dark ? 1 : 0;
        Native.DwmSetWindowAttribute(Handle, 20, ref v, 4);   // DWMWA_USE_IMMERSIVE_DARK_MODE
        v = ColorTranslator.ToWin32(Pal.Bg);
        Native.DwmSetWindowAttribute(Handle, 35, ref v, 4);   // DWMWA_CAPTION_COLOR (Win11)
    }

    void Retheme() {
        Pal.Load(); BackColor = Pal.Bg; ForeColor = Pal.Text; Frame();
        Walk(Controls);
        Invalidate(true);
    }

    static void Walk(Control.ControlCollection cs) {
        foreach (Control c in cs) {
            if (c is W11List) ((W11List)c).Retheme();
            if (c is SearchBox) ((SearchBox)c).Retheme();
            Walk(c.Controls);
        }
    }

    protected override void WndProc(ref Message m) {
        base.WndProc(ref m);
        // WM_SETTINGCHANGE "ImmersiveColorSet" — сменили тему или акцентный цвет
        if (m.Msg == 0x1A && m.LParam != IntPtr.Zero && Marshal.PtrToStringUni(m.LParam) == "ImmersiveColorSet") Retheme();
    }
}

// Строка состояния: то, что в «кавычках» (или “английских”), — жирным. Label так не умеет, поэтому слова
// раскладываются по строкам вручную (в узком окне текст переносится).
class Status : Label {
    const TextFormatFlags F = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

    // Слова одного начертания подряд склеиваются в кусок и рисуются целиком.
    static List<Tuple<string, bool>> Runs(List<Tuple<string, bool>> words) {
        var runs = new List<Tuple<string, bool>>();
        foreach (var w in words)
            if (runs.Count > 0 && runs[runs.Count - 1].Item2 == w.Item2) runs[runs.Count - 1] = Tuple.Create(runs[runs.Count - 1].Item1 + " " + w.Item1, w.Item2);
            else runs.Add(w);
        return runs;
    }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics; g.Clear(Pal.Bg);
        using (var bold = new Font(Font, FontStyle.Bold)) {
            // мерить с g: без контекста рисования MeasureText игнорирует NoPadding (+7 px к каждому куску)
            Func<Tuple<string, bool>, int> width = r => TextRenderer.MeasureText(g, r.Item1, r.Item2 ? bold : Font, Size.Empty, F).Width;
            int space = TextRenderer.MeasureText(g, "a a", Font, Size.Empty, F).Width - TextRenderer.MeasureText(g, "aa", Font, Size.Empty, F).Width;
            Func<List<Tuple<string, bool>>, int> lineWidth = l => { var rs = Runs(l); return rs.Sum(width) + space * (rs.Count - 1); };

            int left = Dpi.S(2);   // по краю карточки: она начинается с обводки
            var lines = new List<List<Tuple<string, bool>>> { new List<Tuple<string, bool>>() };
            bool quoted = false;
            foreach (var w in Text.Split(' ')) {
                if (w.Contains("«") || w.Contains("“")) quoted = true;
                var word = Tuple.Create(w, quoted);
                if (w.Contains("»") || w.Contains("”")) quoted = false;
                var cur = lines[lines.Count - 1];
                if (cur.Count > 0 && left + lineWidth(cur.Concat(new[] { word }).ToList()) > Width) lines.Add(cur = new List<Tuple<string, bool>>());
                cur.Add(word);
            }
            int lh = Font.Height, y = (Height - lh * lines.Count) / 2;
            foreach (var line in lines) {
                int x = left;
                foreach (var r in Runs(line)) {
                    TextRenderer.DrawText(g, r.Item1, r.Item2 ? bold : Font, new Point(x, y), Pal.Sub, F);
                    x += width(r) + space;
                }
                y += lh;
            }
        }
    }
}

// Нарисованные вручную контролы: фон родителя, без мерцания.
class Painted : Control {
    public Painted() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
}


// Карточка со скруглёнными углами, внутри неё список и своя полоса прокрутки.
// Системные полосы не перекрасить и не скруглить, поэтому список шире видимой области
// на ширину своей полосы — она уходит за край и обрезается; поверх рисуется ScrollBarW11.
// Колонки всегда ровно по видимой ширине — горизонтальной полосы нет.
class Card : Panel {
    readonly Panel view = new Panel();
    readonly W11List list;
    readonly ScrollBarW11 bar;

    public Card(W11List lv) {
        list = lv; bar = new ScrollBarW11(lv);   // до Padding: его установка сразу зовёт OnLayout
        DoubleBuffered = true; ResizeRedraw = true; Padding = new Padding(Dpi.S(2), Dpi.S(4), Dpi.S(2), Dpi.S(4));
        view.Controls.Add(bar); view.Controls.Add(lv);
        Controls.Add(view);
        lv.Scrolled += bar.Sync;
    }

    protected override void OnLayout(LayoutEventArgs e) {
        base.OnLayout(e);
        var d = DisplayRectangle;
        view.Bounds = d;
        // квадратная шапка и строки не должны вылезать на скруглённые углы карточки
        using (var p = Pal.Round(new RectangleF(0, 0, d.Width, d.Height), Dpi.S(6))) view.Region = new Region(p);
        // сначала размер (список сам обработает WM_SIZE), потом колонки: наоборот ломается прокрутка
        list.Bounds = new Rectangle(0, 0, d.Width + SystemInformation.VerticalScrollBarWidth, d.Height);
        list.Fit(d.Width);
        int hh = list.HeaderHeight;
        bar.Bounds = new Rectangle(d.Width - ScrollBarW11.W, hh, ScrollBarW11.W, d.Height - hh);
        bar.Sync();
    }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var p = Pal.Round(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Dpi.S(8)))
        using (var b = new SolidBrush(Pal.Card)) using (var pen = new Pen(Pal.Line)) { g.FillPath(b, p); g.DrawPath(pen, p); }
    }
}

class W11Button : Button {
    bool over, down;
    public W11Button() {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
    protected override void OnMouseEnter(EventArgs e) { over = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { over = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics; g.Clear(Pal.Bg); g.SmoothingMode = SmoothingMode.AntiAlias;
        Color fill = Pal.Accent;
        if (down) fill = Pal.Mix(fill, Pal.Bg, 0.2f); else if (over) fill = Pal.Mix(fill, Pal.Bg, 0.1f);
        using (var p = Pal.Round(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Dpi.S(8)))
        using (var b = new SolidBrush(fill)) g.FillPath(b, p);
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }
}

// Полоса прокрутки Windows 11, но бегунок всегда толстый, скруглённый и акцентного цвета.
// Список прокручивается через LVM_SCROLL, бегунок перерисовывается по событию Scrolled.
class ScrollBarW11 : Painted {
    public static int W { get { return Dpi.S(12); } }
    readonly W11List lv;
    bool over, drag;
    int grab;

    public ScrollBarW11(W11List lv) { this.lv = lv; }

    public void Sync() { Visible = lv.IsHandleCreated && lv.Items.Count > lv.PerPage; Invalidate(); }

    Rectangle Thumb() {
        int n = lv.Items.Count, page = lv.PerPage, pad = Dpi.S(2), track = Height - 2 * pad;
        int h = Math.Max(Dpi.S(24), track * page / Math.Max(n, 1));
        int y = pad + (int)((long)(track - h) * lv.TopIndex / Math.Max(n - page, 1));
        int tw = Dpi.S(6);
        return new Rectangle(Width - tw - pad, y, tw, h);
    }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics; g.Clear(Pal.Card); g.SmoothingMode = SmoothingMode.AntiAlias;
        var t = Thumb();
        Color c = drag ? Pal.Mix(Pal.Accent, Pal.Card, 0.25f) : over ? Pal.Mix(Pal.Accent, Color.White, 0.15f) : Pal.Accent;
        using (var p = Pal.Round(t, t.Width / 2f)) using (var b = new SolidBrush(c)) g.FillPath(b, p);
    }

    protected override void OnMouseDown(MouseEventArgs e) {
        base.OnMouseDown(e);
        var t = Thumb();
        if (t.Contains(e.Location) || (e.X >= t.X - Dpi.S(4) && e.Y >= t.Y && e.Y < t.Bottom)) { drag = true; grab = e.Y - t.Y; Invalidate(); }
        else lv.ScrollRows(e.Y < t.Y ? -lv.PerPage : lv.PerPage);   // клик по дорожке — на страницу
    }

    protected override void OnMouseMove(MouseEventArgs e) {
        base.OnMouseMove(e);
        if (!drag) return;
        int n = lv.Items.Count, page = lv.PerPage, pad = Dpi.S(2);
        var t = Thumb();
        double frac = (double)(e.Y - grab - pad) / Math.Max(Height - 2 * pad - t.Height, 1);
        int target = (int)Math.Round(Math.Max(0, Math.Min(1, frac)) * (n - page));
        lv.ScrollRows(target - lv.TopIndex);
    }

    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); drag = false; Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); over = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); over = false; Invalidate(); }
    protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); lv.ScrollRows(-e.Delta / 120 * 3); }
}

// Подпись «Поиск» рисуется сама — системная подсказка (EM_SETCUEBANNER) не умеет жирный шрифт.
class CueBox : TextBox {
    public string Cue;
    protected override void OnHandleCreated(EventArgs e) {
        base.OnHandleCreated(e);
        Native.SendMessage(Handle, 0xD3 /* EM_SETMARGINS */, (IntPtr)3, IntPtr.Zero);   // текст с самого края — ровно по линии списка
    }
    protected override void WndProc(ref Message m) {
        base.WndProc(ref m);
        if (m.Msg != 0x0F /* WM_PAINT */ || TextLength > 0) return;
        using (var g = CreateGraphics()) using (var f = new Font(Font, FontStyle.Bold))
            TextRenderer.DrawText(g, Cue, f, ClientRectangle, Pal.Sub, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }
}

// Поле поиска в стиле Windows 11: скруглённая рамка, акцентная полоска снизу в фокусе, лупа справа.
class SearchBox : Painted {
    public readonly CueBox Box = new CueBox { BorderStyle = BorderStyle.None, Cue = L.T("Поиск", "Search") };

    public SearchBox() {
        Controls.Add(Box);
        Box.GotFocus += delegate { Retheme(); };
        Box.LostFocus += delegate { Retheme(); };
        Retheme();
    }

    // на 0,5 pt крупнее остального текста окна
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Box.Font = new Font(Font.FontFamily, Font.Size + 0.5f); }

    public void Retheme() {
        Box.BackColor = Box.Focused ? Pal.FieldFocus : Pal.Field;
        Box.ForeColor = Pal.Text;
        Invalidate();
    }

    protected override void OnLayout(LayoutEventArgs e) {
        base.OnLayout(e);
        // по линии текста строк: отступ карточки 2 + место под галочку 36 + внутренний отступ,
        // который TextRenderer добавляет к тексту строк (подпись здесь рисуется без него)
        int x = Dpi.S(38) + (TextRenderer.MeasureText("Щ", Font).Width - TextRenderer.MeasureText("Щ", Font, Size.Empty, TextFormatFlags.NoPadding).Width) / 2;
        Box.SetBounds(x, (Height - Box.Height) / 2, Width - x - Dpi.S(10), Box.Height);
    }

    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Box.Focus(); }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics; g.Clear(Pal.Bg); g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var p = Pal.Round(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Dpi.S(8))) {
            using (var b = new SolidBrush(Box.Focused ? Pal.FieldFocus : Pal.Field)) g.FillPath(b, p);
            if (Box.Focused) {
                g.SetClip(p);
                using (var b = new SolidBrush(Pal.Accent)) g.FillRectangle(b, 0, Height - Dpi.S(2), Width, Dpi.S(2));
                g.ResetClip();
            }
        }
        using (var f = new Font(Pal.IconFont, Font.Size * 0.9f))
            // лупа слева, по центру колонки галочек списка (отступ карточки 2 + 12 + 16/2 = 22)
            TextRenderer.DrawText(g, "", f, new Rectangle(Dpi.S(10), 0, Dpi.S(24), Height), Pal.Sub,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

// Пока опрашивается меню: крутящееся кольцо с вырезом и подпись с бегущим многоточием
// в скруглённой рамке. Анимация считается от времени, а не от тиков таймера, — не дёргается.
class Loader : Painted {
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 15 };
    public Loader() { timer.Tick += delegate { Invalidate(); }; timer.Start(); }
    public void Stop() { timer.Stop(); Visible = false; }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics; g.Clear(Pal.Bg); g.SmoothingMode = SmoothingMode.AntiAlias;
        string text = L.T("Загрузка информации пунктов", "Loading menu items");
        int t = Environment.TickCount;
        // место под «...» резервируется всегда, чтобы рамка не прыгала
        int tw = TextRenderer.MeasureText(text, Font, Size.Empty, TextFormatFlags.NoPadding).Width;
        int dw = TextRenderer.MeasureText("...", Font, Size.Empty, TextFormatFlags.NoPadding).Width;
        int ring = Dpi.S(14), pad = Dpi.S(12), gap = Dpi.S(10), h = Dpi.S(30);
        int w = pad + ring + gap + tw + dw + pad;
        var box = new Rectangle((Width - w) / 2, (Height - h) / 2, w, h);

        using (var p = Pal.Round(new RectangleF(box.X + 0.5f, box.Y + 0.5f, box.Width - 1, box.Height - 1), Dpi.S(6)))
        using (var b = new SolidBrush(Pal.Card)) using (var pen = new Pen(Pal.BtnLine)) { g.FillPath(b, p); g.DrawPath(pen, p); }

        using (var pen = new Pen(Pal.Accent, Dpi.K * 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(pen, box.X + pad, box.Y + (h - ring) / 2, ring, ring, t % 900 * 360f / 900, 270);

        var tr = new Rectangle(box.X + pad + ring + gap, box.Y, tw + dw, h);
        TextRenderer.DrawText(g, text + new string('.', t / 400 % 4), Font, tr, Pal.Sub,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    }

}

// Подсказка в цветах окна: штатная ToolTip всегда светлая, даже в тёмной теме.
class W11Tip : ToolTip {
    readonly Font font;
    const TextFormatFlags F = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;
    public W11Tip(Font font) {
        this.font = font;
        OwnerDraw = true;
        Popup += (s, e) => { var sz = TextRenderer.MeasureText(GetToolTip(e.AssociatedControl), font); e.ToolTipSize = new Size(sz.Width + Dpi.S(16), sz.Height + Dpi.S(10)); };
        Draw += (s, e) => {
            // окно подсказки скругляет DWM, радиус как у карточки; рамка — тоже его, цветом обводки
            IntPtr dc = e.Graphics.GetHdc(), hwnd = Native.WindowFromDC(dc);
            e.Graphics.ReleaseHdc(dc);
            int v = 2;   // DWMWCP_ROUND
            Native.DwmSetWindowAttribute(hwnd, 33, ref v, 4);
            v = ColorTranslator.ToWin32(Pal.BtnLine);
            Native.DwmSetWindowAttribute(hwnd, 34 /* DWMWA_BORDER_COLOR */, ref v, 4);
            e.Graphics.Clear(Pal.Card);
            TextRenderer.DrawText(e.Graphics, e.ToolTipText, font, e.Bounds, Pal.Text, F);
        };
    }
}

// Переключатель Windows 11: подпись слева, «таблетка» справа.
class Toggle : Painted {
    public bool Checked;
    bool over;
    public Toggle() { TabStop = true; }

    public void FitWidth() { Width = TextRenderer.MeasureText(Text, Font).Width + Dpi.S(56); }

    protected override void OnClick(EventArgs e) { Checked = !Checked; Invalidate(); base.OnClick(e); }
    protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.KeyCode == Keys.Space) OnClick(e); }
    protected override void OnMouseEnter(EventArgs e) { over = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { over = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics; g.Clear(Pal.Bg);
        float tw = Dpi.S(40), th = Dpi.S(20), x = Width - tw - 1, y = (Height - th) / 2f;
        TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, (int)x - Dpi.S(12), Height), Pal.Text,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float knob = Dpi.S(over ? 14 : 12), kx = Checked ? x + tw - th / 2 : x + th / 2, ky = y + th / 2;
        using (var p = Pal.Round(new RectangleF(x, y, tw, th), th / 2)) {
            if (Checked) using (var b = new SolidBrush(over ? Pal.Mix(Pal.Accent, Pal.Bg, 0.1f) : Pal.Accent)) g.FillPath(b, p);
            else using (var b = new SolidBrush(Pal.BoxFill)) using (var pen = new Pen(Pal.Box)) { g.FillPath(b, p); g.DrawPath(pen, p); }
        }
        using (var b = new SolidBrush(Checked ? Pal.OnAccent : Pal.Box)) g.FillEllipse(b, kx - knob / 2, ky - knob / 2, knob, knob);
    }
}

class Row : ListViewItem {
    public bool On;   // галочка = пункт есть в меню
    public readonly Item Data;
    public Row(Item it) : base(new[] { it.Name, string.Join(", ", it.Where), it.Program }) { Data = it; On = !it.Hidden; }
}

// Список целиком рисуется сам: штатный ListView в тёмной теме остаётся светлым.
// Клик по строке или пробел переключают галочку.
class W11List : ListView {
    Row hot;
    public List<Row> All = new List<Row>();
    const TextFormatFlags Flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

    public W11List() {
        OwnerDraw = true; View = View.Details; FullRowSelect = true; MultiSelect = false; DoubleBuffered = true;
        BorderStyle = BorderStyle.None; HeaderStyle = ColumnHeaderStyle.Nonclickable;
        SmallImageList = new ImageList { ImageSize = new Size(1, Dpi.S(30)) };   // высота строки
        BackColor = Pal.Card; ForeColor = Pal.Text;
    }

    public void SetRows(IEnumerable<Row> rows) { All = rows.ToList(); Filter(""); }

    public void Filter(string q) {
        BeginUpdate();
        hot = null;
        Items.Clear();
        Items.AddRange(All.Where(r => q.Length == 0 || r.SubItems.Cast<ListViewItem.ListViewSubItem>()
            .Any(s => s.Text.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0)).ToArray());
        EndUpdate();
        if (Scrolled != null) Scrolled();
    }

    public event Action Scrolled;   // сменилась позиция или число строк — своей полосе прокрутки
    public int TopIndex { get { return (int)Native.SendMessage(Handle, 0x1027 /* LVM_GETTOPINDEX */, IntPtr.Zero, IntPtr.Zero); } }
    public int PerPage { get { return (int)Native.SendMessage(Handle, 0x1028 /* LVM_GETCOUNTPERPAGE */, IntPtr.Zero, IntPtr.Zero); } }
    public void ScrollRows(int n) {
        if (n != 0 && Items.Count > 0) Native.SendMessage(Handle, 0x1014 /* LVM_SCROLL */, IntPtr.Zero, (IntPtr)(n * Items[0].Bounds.Height));
    }

    public int HeaderHeight {
        get {
            RECT r;
            return IsHandleCreated && Native.GetClientRect(Native.SendMessage(Handle, 0x101F /* LVM_GETHEADER */, IntPtr.Zero, IntPtr.Zero), out r) ? r.Bottom : 0;
        }
    }

    // видимая часть: список шире неё на системную полосу прокрутки (см. Card)
    int VisibleWidth { get { return Parent == null ? ClientSize.Width : Parent.ClientSize.Width; } }

    public void Retheme() {
        BackColor = Pal.Card; ForeColor = Pal.Text;
        if (!IsHandleCreated) return;
        // тёмные полосы прокрутки и фон шапки за последней колонкой
        Native.SetWindowTheme(Handle, Pal.Dark ? "DarkMode_Explorer" : "Explorer", null);
        IntPtr header = Native.SendMessage(Handle, 0x101F /* LVM_GETHEADER */, IntPtr.Zero, IntPtr.Zero);
        Native.SetWindowTheme(header, Pal.Dark ? "DarkMode_ItemsView" : "ItemsView", null);
        // границы колонок не тянутся: сумма вылезла бы за край (горизонтальная полоса), колонки подгоняет Fit
        Native.SetWindowLong(header, -16 /* GWL_STYLE */, Native.GetWindowLong(header, -16) | 0x0800 /* HDS_NOSIZING */);
        Invalidate(true);
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Retheme(); }

    protected override void WndProc(ref Message m) {
        if (m.Msg == 0x204E /* WM_REFLECT + WM_NOTIFY */) {
            var nm = (NMCUSTOMDRAW)Marshal.PtrToStructure(m.LParam, typeof(NMCUSTOMDRAW));
            if (nm.hdr.code == -12 /* NM_CUSTOMDRAW */ && nm.hdr.hwndFrom == Handle) {
                if (nm.stage == 1 /* CDDS_PREPAINT */) { m.Result = (IntPtr)0x20 /* CDRF_NOTIFYITEMDRAW */; return; }
                if (nm.stage == 0x10001 /* CDDS_ITEMPREPAINT */) {
                    int i = (int)nm.item;
                    if (i >= 0 && i < Items.Count) using (var g = Graphics.FromHdc(nm.hdc)) PaintRow(g, (Row)Items[i]);
                    m.Result = (IntPtr)4 /* CDRF_SKIPDEFAULT */;
                    return;
                }
            }
        }
        base.WndProc(ref m);
        // WM_VSCROLL, WM_MOUSEWHEEL, WM_KEYDOWN, LVM_SCROLL, WM_SIZE
        if ((m.Msg == 0x115 || m.Msg == 0x20A || m.Msg == 0x100 || m.Msg == 0x1014 || m.Msg == 0x05) && Scrolled != null) Scrolled();
        // Тема Explorer рисует линии колонок и под последней строкой — закрашиваем пустую часть
        // фоном карточки. Шапку заливка не задевает: у окна WS_CLIPCHILDREN.
        if (m.Msg != 0x0F /* WM_PAINT */) return;
        int top = Items.Count > 0 ? Items[Items.Count - 1].Bounds.Bottom : 0;
        if (top >= ClientSize.Height) return;
        using (var g = CreateGraphics()) using (var b = new SolidBrush(Pal.Card))
            g.FillRectangle(b, 0, top, ClientSize.Width, ClientSize.Height - top);
    }

    // Первая колонка занимает остаток видимой ширины. Звать только после того, как список
    // обработал новый размер: смена ширины колонки до WM_SIZE ломала ему прокрутку (если он был
    // прокручен вниз и окно развернули, верхней строкой становилась -15, строки съезжали и пропадали).
    // Сумма колонок никогда не больше видимой ширины, иначе вылезает горизонтальная полоса:
    // в узком окне сначала ужимается «Где», потом «Программа». Исходные ширины — из Columns.Add.
    int[] widths;
    public void Fit(int visible) {
        if (Columns.Count != 3) return;
        if (widths == null) widths = Columns.Cast<ColumnHeader>().Select(c => c.Width).ToArray();
        int min = Dpi.S(200);   // названия пунктов важнее «Где»
        int where = Math.Max(0, Math.Min(widths[1], visible - min - widths[2]));
        int prog = Math.Max(0, Math.Min(widths[2], visible - min - where));
        Columns[1].Width = where;
        Columns[2].Width = prog;
        Columns[0].Width = Math.Max(0, visible - where - prog);
    }

    protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e) {
        var g = e.Graphics; var r = e.Bounds;
        using (var b = new SolidBrush(Pal.Card)) g.FillRectangle(b, r);
        using (var p = new Pen(Pal.Line)) g.DrawLine(p, r.Left, r.Bottom - 1, r.Right, r.Bottom - 1);
        int pad = e.ColumnIndex == 0 ? Dpi.S(36) : Dpi.S(8);
        TextRenderer.DrawText(g, e.Header.Text, Font, new Rectangle(r.X + pad, r.Y, r.Width - pad, r.Height), Pal.Sub, Flags);
    }

    // Строки рисуются целиком через NM_CUSTOMDRAW с CDRF_SKIPDEFAULT (см. WndProc):
    // при штатном OwnerDraw тема Explorer успевает положить поверх фона свою квадратную
    // подсветку наведения и линии колонок. OwnerDraw остаётся только ради шапки.
    void PaintRow(Graphics g, Row row) {
        var r = row.Bounds;
        using (var b = new SolidBrush(Pal.Card)) g.FillRectangle(b, r);
        int right = VisibleWidth - ScrollBarW11.W;   // правее — своя полоса прокрутки
        if (row == hot || (Focused && ShowFocusCues && row.Focused && hot == null)) {
            var h = r; h.Inflate(-Dpi.S(4), -Dpi.S(1));
            h.Width = Math.Min(h.Width, right - h.X);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = Pal.Round(h, Dpi.S(4))) using (var b = new SolidBrush(Pal.Hover)) g.FillPath(b, p);
            g.SmoothingMode = SmoothingMode.None;
        }
        int size = Dpi.S(16);
        DrawCheck(g, new Rectangle(r.X + Dpi.S(12), r.Y + (r.Height - size) / 2, size, size), row.On);
        TextRenderer.DrawText(g, row.Text, Font, new Rectangle(r.X + Dpi.S(36), r.Y, Columns[0].Width - Dpi.S(40), r.Height), row.On ? Pal.Text : Pal.Sub, Flags);
        int x = r.X + Columns[0].Width;
        for (int i = 1; i < Columns.Count; i++) {
            TextRenderer.DrawText(g, row.SubItems[i].Text, Font, new Rectangle(x + Dpi.S(8), r.Y, Math.Min(Columns[i].Width - Dpi.S(12), right - x - Dpi.S(8)), r.Height), Pal.Sub, Flags);
            x += Columns[i].Width;
        }
    }

    static void DrawCheck(Graphics g, Rectangle b, bool on) {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var p = Pal.Round(new RectangleF(b.X + 0.5f, b.Y + 0.5f, b.Width - 1, b.Height - 1), Dpi.S(4))) {
            if (on) {
                using (var br = new SolidBrush(Pal.Accent)) g.FillPath(br, p);
                using (var pen = new Pen(Pal.OnAccent, Dpi.K * 1.5f)) {
                    pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
                    float w = b.Width;
                    g.DrawLines(pen, new[] { new PointF(b.X + w * 0.26f, b.Y + w * 0.52f), new PointF(b.X + w * 0.43f, b.Y + w * 0.69f), new PointF(b.X + w * 0.75f, b.Y + w * 0.33f) });
                }
            } else {
                using (var br = new SolidBrush(Pal.BoxFill)) g.FillPath(br, p);
                using (var pen = new Pen(Pal.Box)) g.DrawPath(pen, p);
            }
        }
        g.SmoothingMode = SmoothingMode.None;
    }

    void SetHot(Row r) {
        if (r == hot) return;
        if (hot != null) Invalidate(hot.Bounds);
        hot = r;
        if (hot != null) Invalidate(hot.Bounds);
    }

    void Toggle(Row r) { r.On = !r.On; Invalidate(r.Bounds); }

    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); SetHot(GetItemAt(e.X, e.Y) as Row); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); SetHot(null); }
    protected override void OnMouseClick(MouseEventArgs e) { base.OnMouseClick(e); ToggleAt(e); }
    // второй щелчок быстрого двойного приходит как двойной, а не как клик
    protected override void OnMouseDoubleClick(MouseEventArgs e) { base.OnMouseDoubleClick(e); ToggleAt(e); }
    void ToggleAt(MouseEventArgs e) {
        var r = GetItemAt(e.X, e.Y) as Row;
        if (e.Button == MouseButtons.Left && r != null) Toggle(r);
    }
    protected override void OnKeyDown(KeyEventArgs e) {
        base.OnKeyDown(e);
        var r = FocusedItem as Row;
        if (e.KeyCode == Keys.Space && r != null) { Toggle(r); e.Handled = true; }
    }
}

static class Program {
    [STAThread]
    static void Main(string[] args) {
        if (args.Length == 2 && args[0] == "--dump") {
            File.WriteAllLines(args[1], Scan.Run().Select(i => string.Join("\t", i.Name, string.Join(", ", i.Where), i.Program,
                string.Join(" ", i.Targets.Select(t => t.Value + (t.Hidden ? "*" : ""))))));
            return;
        }
        Native.SetProcessDPIAware();
        using (var g = Graphics.FromHwnd(IntPtr.Zero)) Dpi.K = g.DpiX / 96f;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(Build());
    }

    static Form Build() {
        const string title = "ContextMenuCleaner";
        string hint = L.T("Снимите галочки с ненужных пунктов и нажмите «Применить» — Проводник перезапустится.",
                          "Uncheck the items you don't need and press “Apply” — Explorer will restart.");
        bool variable = new System.Drawing.Text.InstalledFontCollection().Families.Any(f => f.Name == "Segoe UI Variable Text");
        Func<int, int> S = v => Dpi.S(v);

        var form = new W11Form {
            Text = title, Size = new Size(S(820), S(580)), MinimumSize = new Size(S(560), S(360)), StartPosition = FormStartPosition.CenterScreen,
            Font = new Font(variable ? "Segoe UI Variable Text" : "Segoe UI", 9), Padding = new Padding(S(6), S(12), S(6), 0),
            Icon = new Icon(typeof(Program).Assembly.GetManifestResourceStream("app.ico")),
        };

        var top = new Panel { Dock = DockStyle.Top, Height = S(44), Padding = new Padding(0, 0, 0, S(10)) };
        var search = new SearchBox { Dock = DockStyle.Left, Width = S(260) };
        var classic = new Toggle { Dock = DockStyle.Right, Text = L.T("Классическое меню", "Classic menu"), Checked = Classic.On, Visible = Classic.Supported };
        var loader = new Loader { Dock = DockStyle.Fill };
        top.Controls.AddRange(new Control[] { loader, search, classic });   // Fill первым: докинг идёт с конца коллекции
        new W11Tip(form.Font).SetToolTip(classic, L.T("Классический вид контекстного меню ПКМ", "Classic right-click context menu"));

        var lv = new W11List();
        lv.Columns.Add(L.T("Пункт меню", "Menu item"), S(300));
        lv.Columns.Add(L.T("Где", "Where"), S(250));
        lv.Columns.Add(L.T("Программа", "Program"), S(140));
        var card = new Card(lv) { Dock = DockStyle.Fill };

        var foot = new Panel { Dock = DockStyle.Bottom, Height = S(56) };
        var status = new Status { Dock = DockStyle.Fill };
        var slot = new Panel { Dock = DockStyle.Right, Width = S(120), Padding = new Padding(0, S(12), 0, S(12)) };
        // как «Поиск»: на 0,5 pt крупнее текста окна и жирный
        var apply = new W11Button { Dock = DockStyle.Fill, Text = L.T("Применить", "Apply"), Font = new Font(form.Font.FontFamily, form.Font.Size + 0.5f, FontStyle.Bold) };
        slot.Controls.Add(apply);
        foot.Controls.AddRange(new Control[] { status, slot });

        search.Box.TextChanged += delegate { lv.Filter(search.Box.Text.Trim()); };

        apply.Click += delegate {
            int n = 0;
            var denied = new List<string>();
            foreach (var row in lv.All) {
                bool changed = false;
                bool hide = !row.On;
                foreach (var t in row.Data.Targets.Where(t => t.Hidden != hide)) {
                    // CreateSubKey открывает существующий ключ как есть.
                    using (var k = Registry.CurrentUser.CreateSubKey(t.Sub)) {
                        if (hide) k.SetValue(t.Value, row.Text); else k.DeleteValue(t.Value, false);
                    }
                    if (!hide && Scan.MachineBlocked(t.Value) && !Scan.MachineUnblock(t.Value)) continue;
                    t.Hidden = hide;
                    changed = true;
                }
                if (!hide && row.Data.Hidden) { row.On = false; denied.Add(row.Text); }   // вернуть не удалось
                if (changed) n++;
            }
            if (classic.Visible && classic.Checked != Classic.On) { Classic.On = classic.Checked; n++; }
            lv.Invalidate();
            string warn = denied.Count == 0 ? "" : L.T(" Нужны права администратора, чтобы вернуть: ", " Run as administrator to restore: ") + string.Join(", ", denied);
            if (n == 0) { status.Text = L.T("Изменений нет.", "Nothing to change.") + warn; return; }
            status.Text = L.T("Перезапуск Проводника…", "Restarting Explorer…");
            status.Refresh();
            RestartExplorer();
            status.Text = L.T("Готово. Изменений: ", "Done. Changes: ") + n + "." + warn;
        };

        // Меню опрашивается в отдельном STA-потоке, чтобы окно появилось сразу.
        form.Shown += delegate {
            var t = new Thread(() => {
                var list = Scan.Run();
                form.BeginInvoke((Action)(() => { lv.SetRows(list.Select(i => new Row(i))); status.Text = hint; loader.Stop(); }));
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
        };

        // Fill добавляется первым: докинг обрабатывается с конца коллекции.
        form.Controls.AddRange(new Control[] { card, foot, top });
        classic.FitWidth();
        return form;
    }

    static void RestartExplorer() {
        foreach (var p in Process.GetProcessesByName("explorer")) try { p.Kill(); } catch (Exception) { }
        // Обычно Windows поднимает Проводник сама; если за 3 с не поднялся — запускаем.
        for (int i = 0; i < 12; i++) {
            Thread.Sleep(250);
            if (Process.GetProcessesByName("explorer").Length > 0) return;
        }
        Process.Start("explorer.exe");
    }
}
}
