using System.Diagnostics;
using System.Text;
using SlideShowStudio.Core;
using SlideShowStudio.Engine;

namespace SlideShowStudio;

/// <summary>Headless verification harness. Run: SlideShowStudio.exe --selftest
/// Generates test photos, renders AVI/GIF/PNG-sequence via the managed compositor,
/// validates the containers, and probes the WMMR engine DLLs for real HRESULTs.</summary>
internal static class SelfTest
{
    public static int Run(string[] args)
    {
        int failures = 0;
        void Check(bool ok, string label, string detail)
        {
            Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {label}: {detail}");
            if (!ok) failures++;
        }

        Console.WriteLine("=== SlideShow Studio self-test ===");
        Console.WriteLine($"platform: {Environment.Is64BitProcess} (x86 expected: False), tz={TimeZoneInfo.Local.StandardName}");

        var outDir = Path.GetFullPath(args.Length > 1 ? args[1] : "selftest_out");
        Directory.CreateDirectory(outDir);

        // ---- 1. generate test images ----
        var images = new List<string>();
        for (int i = 0; i < 4; i++)
        {
            string p = Path.Combine(outDir, $"test_{i + 1}.png");
            MakeTestImage(p, i, 800, 600);
            images.Add(p);
        }
        Check(images.All(File.Exists), "test image generation", $"{images.Count} PNGs in {outDir}");

        // ---- 2. project + timeline math ----
        var proj = new SlideshowProject
        {
            Name = "Selftest",
            Width = 320,
            Height = 240,
            Fps = 15,
            DefaultSlideMs = 600,
            DefaultTransitionMs = 200,
        };
        proj.Slides.AddRange(new[]
        {
            new Slide { Path = images[0], DurationMs = 600, TransitionMs = 200, Transition = SlideTransition.CrossFade },
            new Slide { Path = images[1], DurationMs = 600, TransitionMs = 200, Transition = SlideTransition.DiagonalWipe, PanZoom = PanZoom.ZoomIn },
            new Slide { Path = images[2], DurationMs = 600, TransitionMs = 200, Transition = SlideTransition.CircleWipe, PanZoom = PanZoom.PanRight, Easing = Easing.EaseOut },
            new Slide { Path = images[3], DurationMs = 600, TransitionMs = 200, Transition = SlideTransition.FadeToBlack },
        });
        long total = proj.TotalDurationMs();
        Check(total == 1800, "timeline math", $"total={total}ms (4×600 − 3×200 = 1800 expected)");

        // ---- 3. project JSON round-trip ----
        string jpath = Path.Combine(outDir, "selftest.ssproj");
        proj.Save(jpath);
        var loaded = SlideshowProject.Load(jpath);
        Check(loaded.SlideCount == 4 && loaded.TotalDurationMs() == total,
            "project JSON round-trip", $"loaded {loaded.SlideCount} slides, total {loaded.TotalDurationMs()}ms");

        // ---- 4. render all three formats ----
        var renderer = new SlideshowRenderer(proj);
        int expectedFrames = (int)Math.Ceiling(total / (1000.0 / proj.Fps));

        string aviPath = Path.Combine(outDir, "selftest.avi");
        var stAvi = renderer.Render(new RenderOptions { OutputPath = aviPath, Kind = OutputKind.Avi });
        Check(stAvi.Frames == expectedFrames, "AVI render", $"frames={stAvi.Frames} bytes={stAvi.Bytes} in {stAvi.ElapsedSec:F2}s");

        string gifPath = Path.Combine(outDir, "selftest.gif");
        var stGif = renderer.Render(new RenderOptions { OutputPath = gifPath, Kind = OutputKind.Gif });
        Check(stGif.Frames == expectedFrames, "GIF render", $"frames={stGif.Frames} bytes={stGif.Bytes} in {stGif.ElapsedSec:F2}s");

        string pngPath = Path.Combine(outDir, "selftest_seq");
        var stPng = renderer.Render(new RenderOptions { OutputPath = pngPath, Kind = OutputKind.PngSequence });
        int pngCount = Directory.Exists(pngPath) ? Directory.GetFiles(pngPath, "*.png").Length : 0;
        Check(pngCount == expectedFrames, "PNG sequence render", $"frames={pngCount} bytes={stPng.Bytes}");

        // ---- 5. container validation ----
        var aviInfo = ReadAviHeader(aviPath);
        Check(aviInfo is not null && aviInfo.Value.Frames == expectedFrames &&
              aviInfo.Value.Width == 320 && aviInfo.Value.Height == 240,
            "AVI container parse", aviInfo is null ? "unreadable" :
            $"w={aviInfo.Value.Width} h={aviInfo.Value.Height} frames={aviInfo.Value.Frames} chunks={aviInfo.Value.Chunks}");

        int gifFrames = CountGifFrames(gifPath);
        Check(gifFrames == expectedFrames, "GIF container parse", $"{gifFrames} image descriptors");

        // ---- 6. transition sanity: frame hashes differ across the timeline ----
        using (var f0 = renderer.RenderFrameAt(0))
        using (var fmid = renderer.RenderFrameAt(900))
        using (var fend = renderer.RenderFrameAt(1799))
        {
            var h0 = BitmapHash(f0);
            var hm = BitmapHash(fmid);
            var he = BitmapHash(fend);
            Check(h0 != he && h0 != hm, "transition variety", $"hashes t0={h0} t900={hm} t1799={he}");
        }

        // ---- 7. ffprobe external validation (if present) ----
        var ff = ProbeFfprobe(aviPath);
        if (ff is null)
            Check(false, "ffprobe validation", "ffprobe not on PATH");
        else
            Check(ff > 0 && Math.Abs(ff - 1.8) < 0.2, "ffprobe AVI duration", $"{ff:F3}s (expect ≈1.8s)");

        // ---- 8. engine probes (real HRESULTs) ----
        Console.WriteLine();
        Console.WriteLine("=== WMMR engine probes ===");
        var engine = new SlideshowEngine();
        var probes = engine.ProbeAll();
        foreach (var p in probes)
            Console.WriteLine(p.ToString());
        int passed = probes.Count(p => p.Pass);
        Check(passed == probes.Count, "engine probes", $"{passed}/{probes.Count} passed");
        Check(engine.EngineDllsPresent, "engine dll dir", engine.DllDirectory ?? "NOT FOUND");

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "SELFTEST RESULT: ALL PASS"
            : $"SELFTEST RESULT: {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    private static void MakeTestImage(string path, int idx, int w, int h)
    {
        using var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        var baseColor = new[]
        {
            Color.FromArgb(30, 90, 170), Color.FromArgb(170, 60, 30),
            Color.FromArgb(40, 150, 70), Color.FromArgb(150, 130, 20),
        }[idx];
        using (var br = new System.Drawing.Drawing2D.LinearGradientBrush(
                   new Rectangle(0, 0, w, h), baseColor, Color.White, 45f))
            g.FillRectangle(br, 0, 0, w, h);
        using var pen = new Pen(Color.FromArgb(255, 255, 255, 255), 12f);
        g.DrawEllipse(pen, 80 + idx * 40, 90, 240, 240);
        g.DrawLine(pen, 40, 470, 760, 470);
        using var font = new Font("Segoe UI", 40f, FontStyle.Bold);
        g.DrawString($"Slide {idx + 1}", font, Brushes.White, 60, 400);
        g.DrawString($"seed {idx}", new Font("Segoe UI", 16f), Brushes.WhiteSmoke, 640, 80);
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static string BitmapHash(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        var sha = System.Security.Cryptography.SHA256.HashData(ms.ToArray());
        return Convert.ToHexString(sha).Substring(0, 12);
    }

    private static (int Width, int Height, int Frames, int Chunks)? ReadAviHeader(string path)
    {
        try
        {
            using var br = new BinaryReader(File.OpenRead(path));
            if (Encoding.ASCII.GetString(br.ReadBytes(4)) != "RIFF") return null;
            br.ReadUInt32();
            if (Encoding.ASCII.GetString(br.ReadBytes(4)) != "AVI ") return null;
            int width = 0, height = 0, frames = 0, chunks = 0;

            void Skip(uint size) => br.BaseStream.Position += size + (size & 1);

            while (br.BaseStream.Position < br.BaseStream.Length)
            {
                string id = Encoding.ASCII.GetString(br.ReadBytes(4));
                uint size = br.ReadUInt32();
                if (id == "avih")
                {
                    br.ReadUInt32(); // dwMicroSecPerFrame
                    br.ReadUInt32(); // dwMaxBytesPerSec
                    br.ReadUInt32(); // dwPaddingGranularity
                    br.ReadUInt32(); // dwFlags
                    frames = br.ReadInt32(); // dwTotalFrames
                    br.BaseStream.Position += size - 5 * 4;
                }
                else if (id == "strf")
                {
                    br.ReadUInt32(); // biSize
                    width = br.ReadInt32();
                    height = Math.Abs(br.ReadInt32());
                    br.BaseStream.Position += size - 3 * 4;
                }
                else if (id == "LIST")
                {
                    string listType = Encoding.ASCII.GetString(br.ReadBytes(4));
                    if (listType == "movi")
                    {
                        long end = br.BaseStream.Position + size - 4;
                        while (br.BaseStream.Position < end)
                        {
                            string cid = Encoding.ASCII.GetString(br.ReadBytes(4));
                            uint csize = br.ReadUInt32();
                            if (cid == "00db") chunks++;
                            Skip(csize);
                        }
                    }
                    else
                    {
                        Skip(size - 4);
                    }
                }
                else
                {
                    Skip(size);
                }
            }
            return (width, height, frames, chunks);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int CountGifFrames(string path)
    {
        int frames = 0;
        using var br = new BinaryReader(File.OpenRead(path));
        if (Encoding.ASCII.GetString(br.ReadBytes(6)) != "GIF89a") return -1;
        br.BaseStream.Position = 2 + 2 + 2 + 1 + 1 + 1;
        while (br.BaseStream.Position < br.BaseStream.Length)
        {
            byte b = br.ReadByte();
            if (b == 0x3B) break; // trailer
            if (b == 0x2C)
            {
                frames++;
                br.BaseStream.Position += 8;
                int packed = br.ReadByte();
                if ((packed & 0x80) != 0)
                    br.BaseStream.Position += 2 * (1 << ((packed & 0x07) + 1));
                int minCodeSize = br.ReadByte();
                while (true)
                {
                    int len = br.ReadByte();
                    if (len == 0) break;
                    br.BaseStream.Position += len;
                }
            }
            else if (b == 0x21)
            {
                int label = br.ReadByte();
                if (label == 0xF9) { br.BaseStream.Position += 1; int len = br.ReadByte(); br.BaseStream.Position += len; }
                else if (label == 0xFF) { int len = br.ReadByte(); br.BaseStream.Position += len; int sub = br.ReadByte(); if (sub == 0) { /* handled */ } else br.BaseStream.Position += 0; }
                else if (label == 0xFE || label == 0x01) { while (true) { int len = br.ReadByte(); if (len == 0) break; br.BaseStream.Position += len; } }
                else break;
            }
            else break;
        }
        return frames;
    }

    private static double? ProbeFfprobe(string avi)
    {
        var psi = new ProcessStartInfo("ffprobe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-show_entries"); psi.ArgumentList.Add("format=duration");
        psi.ArgumentList.Add("-of"); psi.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
        psi.ArgumentList.Add(avi);
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return null;
            string outp = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(10000);
            return double.TryParse(outp, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
