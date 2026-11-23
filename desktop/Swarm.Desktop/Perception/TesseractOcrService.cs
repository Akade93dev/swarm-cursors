// P2 OCR/SoM 视觉兜底 — TesseractOCR 5.5.1 实现 (内嵌 Native, 零用户侧成本)
// NuGet: TesseractOCR 5.5.1 (内嵌 Tesseract 5.x + Leptonica Native)
// 模型文件需放 tessdata/: eng.traineddata (英文) + chi_sim.traineddata (简体中文, 可选)
// 默认 tessdata 路径: publish-verify/tessdata/, 可用 SWARM_TESSDATA_DIR 覆盖

using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Swarm.Core.Capture;
using Swarm.Core.Perception;
using TesseractOCR;
using TesseractOCR.Enums;
using TesseractOCR.Pix;

namespace Swarm.Desktop.Perception;

public sealed class TesseractOcrService : IOcrService, IDisposable
{
    private readonly Lazy<Engine?> _engine;
    private readonly string _tessDataDir;
    private readonly string _languages;

    public TesseractOcrService() : this(null, "eng") { }

    public TesseractOcrService(string? tessDataDir, string languages = "eng+chi_sim")
    {
        _languages = languages;
        _tessDataDir = tessDataDir ?? ResolveDefaultTessDataDir();
        _engine = new Lazy<Engine?>(() =>
        {
            try
            {
                if (!Directory.Exists(_tessDataDir)) return null;
                return new Engine(_tessDataDir, languages, EngineMode.Default);
            }
            catch (Exception)
            {
                return null;
            }
        });
    }

    public bool IsAvailable => _engine.Value != null;

    public async Task<IReadOnlyList<OcrLine>> OcrAsync(RgbaFrame frame, CancellationToken ct = default)
    {
        var eng = _engine.Value;
        if (eng == null || frame == null || frame.Pixels == null || frame.Width <= 0 || frame.Height <= 0)
            return System.Array.Empty<OcrLine>();

        ct.ThrowIfCancellationRequested();
        return await Task.Run(() => RunOcr(eng, frame), ct).ConfigureAwait(false);
    }

    private static List<OcrLine> RunOcr(Engine eng, RgbaFrame frame)
    {
        var pngBytes = NativeImage.ToPngBytes(frame);
        if (pngBytes.Length == 0) return new List<OcrLine>();

        using var pix = Image.LoadFromMemory(pngBytes);
        if (pix == null) return new List<OcrLine>();

        using var page = eng.Process(pix, PageSegMode.SingleLine);
        var result = new List<OcrLine>(16);

        // Page.Layout 树: page.Layout = Layout.Blocks (IEnumerable<Block>) → Paragraphs → TextLines → Words
        foreach (var block in page.Layout)
        foreach (var para in block.Paragraphs)
        foreach (var line in para.TextLines)
        foreach (var word in line.Words)
        {
            var text = word.Text?.Trim();
            if (string.IsNullOrEmpty(text)) continue;
            var rect = word.BoundingBox;
            if (rect == null || rect.Value.Width <= 0 || rect.Value.Height <= 0) continue;
            var r = rect.Value;

            result.Add(new OcrLine(
                Text: text,
                X: (double)r.X1 / frame.Width,
                Y: (double)r.Y1 / frame.Height,
                Width: (double)r.Width / frame.Width,
                Height: (double)r.Height / frame.Height,
                Confidence: Math.Clamp(word.Confidence / 100.0, 0, 1)));
        }
        return result;
    }

    private static string ResolveDefaultTessDataDir()
    {
        var env = Environment.GetEnvironmentVariable("SWARM_TESSDATA_DIR");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) return env;

        var exeDir = AppContext.BaseDirectory;
        var publishTessdata = Path.Combine(exeDir, "tessdata");
        if (Directory.Exists(publishTessdata)) return publishTessdata;

        var repoRoot = FindRepoRoot();
        if (repoRoot != null)
        {
            var srcTessdata = Path.Combine(repoRoot, "desktop", "tessdata");
            if (Directory.Exists(srcTessdata)) return srcTessdata;
        }
        return Path.Combine(exeDir, "tessdata");
    }

    private static string? FindRepoRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir, ".git")) &&
                Directory.Exists(Path.Combine(dir, "desktop")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return null;
    }

    public void Dispose()
    {
        if (_engine.IsValueCreated)
        {
            try { _engine.Value?.Dispose(); }
            catch { /* 忽略 */ }
        }
    }
}
