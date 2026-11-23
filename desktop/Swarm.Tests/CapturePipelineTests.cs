using Swarm.Core.Capture;
using Swarm.Core.Semantics;

namespace Swarm.Tests;

/// <summary>
/// 抓屏后端链的契约：第一个"非空白帧"获胜；全部失败时返回最后一次尝试并如实标记 Blank；
/// 后端异常不被吞掉。测试不依赖真实 HWND 与 Win32。
/// </summary>
public class CapturePipelineTests
{
    private static readonly CaptureGeometry Geometry = new(
        Monitor: new RectInt(0, 0, 1920, 1080),
        ClientScreen: new RectInt(100, 100, 800, 600),
        ClientBounds: new RectInt(0, 0, 800, 600),
        DpiScale: 1.5);

    [Fact]
    public void 首个非空白帧获胜且不再调用后续后端()
    {
        var first = new FakeBackend("wgc", new CaptureAttempt(VariedFrame(), Occluded: true));
        var second = new FakeBackend("print", new CaptureAttempt(VariedFrame()));
        var pipeline = new CapturePipeline(first, second);

        var result = pipeline.Capture(123, Geometry);

        Assert.Equal("wgc", result.Backend);
        Assert.False(result.Blank);
        Assert.True(result.Occluded);
        Assert.Equal(1, first.Calls);
        Assert.Equal(0, second.Calls);
    }

    [Fact]
    public void 首个后端返回空帧时降级到下一个后端()
    {
        var first = new FakeBackend("wgc", new CaptureAttempt(null));
        var second = new FakeBackend("print", new CaptureAttempt(VariedFrame()));
        var pipeline = new CapturePipeline(first, second);

        var result = pipeline.Capture(123, Geometry);

        Assert.Equal("print", result.Backend);
        Assert.False(result.Blank);
        Assert.Equal(1, second.Calls);
    }

    [Fact]
    public void 首个后端返回空白帧时降级到下一个后端()
    {
        var first = new FakeBackend("wgc", new CaptureAttempt(SolidFrame()));
        var second = new FakeBackend("print", new CaptureAttempt(VariedFrame()));
        var pipeline = new CapturePipeline(first, second);

        var result = pipeline.Capture(123, Geometry);

        Assert.Equal("print", result.Backend);
        Assert.False(result.Blank);
    }

    [Fact]
    public void 全部后端都是空白时返回最后一次尝试并标记空白()
    {
        var first = new FakeBackend("wgc", new CaptureAttempt(SolidFrame()));
        var second = new FakeBackend("screen", new CaptureAttempt(SolidFrame(), Occluded: true));
        var pipeline = new CapturePipeline(first, second);

        var result = pipeline.Capture(123, Geometry);

        Assert.Equal("screen", result.Backend);
        Assert.True(result.Blank);
        Assert.True(result.Occluded);
        Assert.NotNull(result.Frame);
    }

    [Fact]
    public void 后端异常向上抛出而不是被吞掉()
    {
        var pipeline = new CapturePipeline(new ThrowingBackend("wgc"));

        Assert.Throws<InvalidOperationException>(() => pipeline.Capture(123, Geometry));
    }

    [Fact]
    public void 窗口句柄与几何信息原样传给后端()
    {
        var backend = new FakeBackend("wgc", new CaptureAttempt(VariedFrame()));
        var pipeline = new CapturePipeline(backend);

        _ = pipeline.Capture(456, Geometry);

        Assert.Equal(456, backend.SeenWindow);
        Assert.Equal(Geometry, backend.SeenGeometry);
    }

    [Fact]
    public void 空后端列表在构造时被拒绝()
    {
        Assert.Throws<ArgumentException>(() => new CapturePipeline());
    }

    [Fact]
    public void 获胜后端的帧龄随结果透传()
    {
        var first = new FakeBackend("wgc", new CaptureAttempt(VariedFrame(), FrameAgeMs: 42));
        var second = new FakeBackend("print", new CaptureAttempt(VariedFrame(), FrameAgeMs: 7));
        var pipeline = new CapturePipeline(first, second);

        var result = pipeline.Capture(123, Geometry);

        Assert.Equal("wgc", result.Backend);
        Assert.Equal(42, result.FrameAgeMs);
    }

    [Fact]
    public void 全部失败时透传最后一次帧龄()
    {
        var first = new FakeBackend("wgc", new CaptureAttempt(null));
        var second = new FakeBackend("screen", new CaptureAttempt(SolidFrame(), Occluded: true, FrameAgeMs: 9));
        var pipeline = new CapturePipeline(first, second);

        var result = pipeline.Capture(123, Geometry);

        Assert.Equal("screen", result.Backend);
        Assert.Equal(9, result.FrameAgeMs);
    }

    [Fact]
    public void 三段降级时屏幕裁剪获胜并透传遮挡标记()
    {
        var wgc = new FakeBackend("wgc", new CaptureAttempt(SolidFrame()));
        var print = new FakeBackend("print", new CaptureAttempt(null));
        var screen = new FakeBackend("screen", new CaptureAttempt(VariedFrame(), Occluded: true));
        var pipeline = new CapturePipeline(wgc, print, screen);

        var result = pipeline.Capture(123, Geometry);

        Assert.Equal("screen", result.Backend);
        Assert.False(result.Blank);
        Assert.True(result.Occluded);
        Assert.Equal(1, screen.Calls);
    }

    [Fact]
    public void 三段后端全部空白时返回最后一次尝试()
    {
        var wgc = new FakeBackend("wgc", new CaptureAttempt(null));
        var print = new FakeBackend("print", new CaptureAttempt(SolidFrame()));
        var screen = new FakeBackend("screen", new CaptureAttempt(SolidFrame(), FrameAgeMs: 3));
        var pipeline = new CapturePipeline(wgc, print, screen);

        var result = pipeline.Capture(123, Geometry);

        Assert.Equal("screen", result.Backend);
        Assert.True(result.Blank);
        Assert.Equal(3, result.FrameAgeMs);
        Assert.NotNull(result.Frame);
    }

    private static RgbaFrame SolidFrame()
    {
        var pixels = new byte[4 * 4 * 4];
        for (var i = 0; i < pixels.Length; i += 4) pixels[i + 3] = 255;
        return new RgbaFrame(4, 4, pixels);
    }

    private static RgbaFrame VariedFrame()
    {
        var pixels = new byte[4 * 4 * 4];
        for (var i = 0; i < pixels.Length; i += 4) pixels[i + 3] = 255;
        pixels[0] = 255;                 // 红
        pixels[4 + 1] = 255;             // 绿
        pixels[8 + 2] = 255;             // 蓝
        return new RgbaFrame(4, 4, pixels);
    }

    private sealed class FakeBackend : ICaptureBackend
    {
        private readonly Queue<CaptureAttempt> _attempts;

        public FakeBackend(string name, params CaptureAttempt[] attempts)
        {
            Name = name;
            _attempts = new Queue<CaptureAttempt>(attempts);
        }

        public string Name { get; }
        public int Calls { get; private set; }
        public nint SeenWindow { get; private set; }
        public CaptureGeometry? SeenGeometry { get; private set; }

        public CaptureAttempt TryCapture(nint window, CaptureGeometry geometry)
        {
            Calls++;
            SeenWindow = window;
            SeenGeometry = geometry;
            return _attempts.Count > 0
                ? _attempts.Dequeue()
                : throw new InvalidOperationException($"后端 {Name} 没有预设结果");
        }
    }

    private sealed class ThrowingBackend(string name) : ICaptureBackend
    {
        public string Name { get; } = name;

        public CaptureAttempt TryCapture(nint window, CaptureGeometry geometry)
            => throw new InvalidOperationException("抓屏失败");
    }
}
