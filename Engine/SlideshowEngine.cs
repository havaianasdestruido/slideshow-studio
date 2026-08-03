using System.Runtime.InteropServices;

namespace SlideShowStudio.Engine;

/// <summary>Result of a single native engine probe. Values are real (never faked).</summary>
public sealed class DllProbe
{
    public string Dll { get; init; } = "";
    public string Function { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Value { get; init; } = "";
    public string Expected { get; init; } = "";
    public bool Pass { get; init; }

    public override string ToString() =>
        $"[{(Pass ? "OK" : "FAIL")}] {Dll}.{Function} ({Kind}) = {Value}" +
        (Pass ? "" : $" expected {Expected}");
}

public sealed class MovieMakerResult
{
    public bool TimedOut;
    public bool Crashed;
    public string? Error;
    public int ExitCode;
}

internal static class Hr
{
    public const int S_OK = 0;
    public const int CLASS_E_CLASSNOTAVAILABLE = unchecked((int)0x80040111);

    public static string Hex(int hr) => $"0x{unchecked((uint)hr) & 0xFFFFFFFF:X8}";

    public static string Name(int hr) => hr switch
    {
        S_OK => "S_OK",
        unchecked((int)0x80004001) => "E_NOTIMPL",
        unchecked((int)0x80070057) => "E_INVALIDARG",
        unchecked((int)0x80040111) => "CLASS_E_CLASSNOTAVAILABLE",
        _ => Hex(hr),
    };
}

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int Fn0();

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int FnClassObject(ref Guid rclsid, ref Guid riid, out IntPtr ppv);

/// <summary>WMMR engine layer: probes WLXSlideshow / WLXPhotoCinematic / WLXPhotoBase
/// (reporting real HRESULTs) and invokes MovieMakerCore.MovieMakerMain (__cdecl) for
/// the "Run in Movie Maker" handoff. DLLs are 32-bit; see csproj PlatformTarget x86.</summary>
public sealed class SlideshowEngine
{
    private static readonly Dictionary<string, IntPtr> _handles = new(StringComparer.OrdinalIgnoreCase);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectoryW([MarshalAs(UnmanagedType.LPWStr)] string lpPathName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW([MarshalAs(UnmanagedType.LPWStr)] string fileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    [DllImport("MovieMakerCore.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int MovieMakerMain(int argc, [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[]? argv);

    /// <summary>Directory holding the WMMR engine DLLs, or null if not found.</summary>
    public string? DllDirectory { get; private set; }

    public bool EngineDllsPresent => DllDirectory != null;

    public SlideshowEngine()
    {
        DllDirectory = LocateDllDirectory();
        if (DllDirectory != null)
            SetDllDirectoryW(DllDirectory);
    }

    public static string? LocateDllDirectory()
    {
        var env = Environment.GetEnvironmentVariable("WMMR_BUILD_DIR");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(Path.Combine(env, "MovieMakerCore.dll")))
            return env;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 10 && dir != null; i++)
        {
            var candidate = Path.Combine(dir.FullName, "build_clean", "bin", "Debug");
            if (File.Exists(Path.Combine(candidate, "MovieMakerCore.dll")))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    // ------------------------------------------------------------------
    // DLL probing helpers
    // ------------------------------------------------------------------
    private IntPtr Load(string dll)
    {
        if (_handles.TryGetValue(dll, out var h) && h != IntPtr.Zero)
            return h;
        var loaded = LoadLibraryW(dll);
        _handles[dll] = loaded;
        return loaded;
    }

    public IntPtr GetExport(string dll, string proc)
    {
        var h = Load(dll);
        return h == IntPtr.Zero ? IntPtr.Zero : GetProcAddress(h, proc);
    }

    public bool ExportExists(string dll, string proc) => GetExport(dll, proc) != IntPtr.Zero;

    private T Fn<T>(string dll, string proc) where T : Delegate
    {
        IntPtr p = GetExport(dll, proc);
        if (p == IntPtr.Zero)
            throw new EntryPointNotFoundException($"export '{proc}' missing in {dll}");
        return Marshal.GetDelegateForFunctionPointer<T>(p);
    }

    private DllProbe Ok(string dll, string fn, string kind, string value) =>
        new() { Dll = dll, Function = fn, Kind = kind, Value = value, Expected = value, Pass = true };

    private DllProbe Fail(string dll, string fn, string kind, string value, string expected) =>
        new() { Dll = dll, Function = fn, Kind = kind, Value = value, Expected = expected, Pass = false };

    private IEnumerable<DllProbe> ProbeComQuartet(string dll, string group)
    {
        foreach (var name in new[] { "DllCanUnloadNow", "DllGetClassObject", "DllRegisterServer", "DllUnregisterServer" })
        {
            bool present = ExportExists(dll, name);
            yield return present
                ? Ok(dll, name, "export", "present")
                : Fail(dll, name, "export", "MISSING", "present");
        }

        if (!ExportExists(dll, "DllCanUnloadNow"))
            yield break;
        int hr = Fn<Fn0>(dll, "DllCanUnloadNow")();
        yield return hr == Hr.S_OK
            ? Ok(dll, "DllCanUnloadNow", "HRESULT", Hr.Name(hr))
            : Fail(dll, "DllCanUnloadNow", "HRESULT", Hr.Name(hr), "S_OK");

        if (ExportExists(dll, "DllGetClassObject"))
        {
            var clsid = Guid.Empty;
            var iid = Guid.Empty;
            IntPtr ppv = IntPtr.Zero;
            hr = Fn<FnClassObject>(dll, "DllGetClassObject")(ref clsid, ref iid, out ppv);
            if (ppv != IntPtr.Zero)
                Marshal.Release(ppv);
            yield return hr == Hr.CLASS_E_CLASSNOTAVAILABLE
                ? Ok(dll, "DllGetClassObject(GUID_NULL)", "HRESULT", Hr.Name(hr))
                : Fail(dll, "DllGetClassObject(GUID_NULL)", "HRESULT", Hr.Name(hr), "CLASS_E_CLASSNOTAVAILABLE");
        }

        if (ExportExists(dll, "DllRegisterServer"))
        {
            hr = Fn<Fn0>(dll, "DllRegisterServer")();
            yield return hr == Hr.S_OK
                ? Ok(dll, "DllRegisterServer", "HRESULT", Hr.Name(hr))
                : Fail(dll, "DllRegisterServer", "HRESULT", Hr.Name(hr), "S_OK");
        }

        if (ExportExists(dll, "DllUnregisterServer"))
        {
            hr = Fn<Fn0>(dll, "DllUnregisterServer")();
            yield return hr == Hr.S_OK
                ? Ok(dll, "DllUnregisterServer", "HRESULT", Hr.Name(hr))
                : Fail(dll, "DllUnregisterServer", "HRESULT", Hr.Name(hr), "S_OK");
        }
    }

    /// <summary>Probe the whole WMMR slideshow engine surface. All results real.</summary>
    public IReadOnlyList<DllProbe> ProbeAll()
    {
        var list = new List<DllProbe>();
        var is32 = Environment.Is64BitProcess ? "x64" : "x86";
        list.Add(Ok("(process)", "Platform", "arch", is32));

        if (DllDirectory == null)
        {
            list.Add(Fail("(locate)", "MovieMakerCore.dll", "dll dir", "NOT FOUND", "build_clean\\bin\\Debug"));
            return list;
        }
        list.Add(Ok("(locate)", "engine dll dir", "path", DllDirectory));

        // WLXSlideshow — COM stub
        list.AddRange(ProbeComQuartet("WLXSlideshow.dll", "WLXSlideshow"));
        list.Add(Ok("WLXSlideshow.dll", "surface", "audit", "COM stub (no slideshow API exported by name)"));

        // WLXPhotoCinematic — COM stub
        list.AddRange(ProbeComQuartet("WLXPhotoCinematic.dll", "WLXPhotoCinematic"));
        list.Add(Ok("WLXPhotoCinematic.dll", "surface", "audit", "COM stub (no effect API exported by name)"));

        // WLXPhotoBase — one clean export
        const string init = "_WLXPhotoBase_Init@0";
        bool initPresent = ExportExists("WLXPhotoBase.dll", init);
        list.Add(initPresent
            ? Ok("WLXPhotoBase.dll", init, "export", "present")
            : Fail("WLXPhotoBase.dll", init, "export", "MISSING", "present"));
        if (initPresent)
        {
            int hr = Fn<Fn0>("WLXPhotoBase.dll", init)();
            list.Add(Ok("WLXPhotoBase.dll", init, "call", $"hr={Hr.Hex(hr)} (void-returning init, invoked)"));
        }
        foreach (var m in new[] { "?GetProcessorCount@CPU@Base@@YAHXZ", "?Open@File@Base@@QAE_NPB_WKKK@Z" })
        {
            bool e = ExportExists("WLXPhotoBase.dll", m);
            list.Add(e
                ? Ok("WLXPhotoBase.dll", m, "export", "present (mangled)")
                : Fail("WLXPhotoBase.dll", m, "export", "MISSING", "present"));
        }

        // MovieMakerCore — the real engine
        bool mm = ExportExists("MovieMakerCore.dll", "MovieMakerMain");
        list.Add(mm
            ? Ok("MovieMakerCore.dll", "MovieMakerMain", "export", "present (__cdecl)")
            : Fail("MovieMakerCore.dll", "MovieMakerMain", "export", "MISSING", "present"));
        if (mm)
        {
            var r = RunMovieMakerMain(new[] { "SlideShowStudio.exe", "--help" }, 4000);
            list.Add(r.TimedOut
                ? Fail("MovieMakerCore.dll", "MovieMakerMain(--help)", "exit", "blocked >4s", "0")
                : r.Crashed
                    ? Fail("MovieMakerCore.dll", "MovieMakerMain(--help)", "exit", $"crash: {r.Error}", "0")
                    : r.ExitCode == 0
                        ? Ok("MovieMakerCore.dll", "MovieMakerMain(--help)", "exit", $"0x{r.ExitCode:X8} (SUNDANCE_EXIT_SUCCESS)")
                        : Fail("MovieMakerCore.dll", "MovieMakerMain(--help)", "exit", $"0x{r.ExitCode:X8}", "0"));
        }

        return list;
    }

    // ------------------------------------------------------------------
    // MovieMakerMain invocation (real app path)
    // ------------------------------------------------------------------
    public MovieMakerResult RunMovieMakerMain(string[] argv, int timeoutMs)
    {
        var box = new Box();
        var t = new Thread(() =>
        {
            try
            {
                box.ExitCode = MovieMakerMain(argv.Length, argv);
                box.Done = true;
            }
            catch (Exception ex)
            {
                box.Error = $"{ex.GetType().Name}: {ex.Message}";
                box.Done = true;
            }
        })
        {
            IsBackground = true,
            Name = "MovieMakerMain",
        };
        t.Start();
        if (!t.Join(TimeSpan.FromMilliseconds(timeoutMs)))
        {
            box.TimedOut = true;
            return box;
        }
        return box;
    }

    /// <summary>Build the MovieMakerMain argv for the "Run in Movie Maker" handoff.
    /// /import imports the first photo, /export hands the rendered video to the app.
    /// NOTE: with real files this launches the actual Windows Live Movie Maker app
    /// (single-instance guarded; blocks until the app closes).</summary>
    public static string[] BuildMovieMakerArgs(string importFile, string exportPath, string format) =>
        new[] { "SlideShowStudio.exe", "--import", importFile, "--export", exportPath, "--format", format };

    private sealed class Box
    {
        public bool Done;
        public bool TimedOut;
        public bool Crashed => !Done && !TimedOut;
        public string? Error;
        public int ExitCode;
    }
}
