using Swarm.Core.Security;

namespace Swarm.Tests;

public class TaskAuthorizationTests
{
    [Fact]
    public void 授权令牌必须匹配当前任务与目标进程()
    {
        var tasks = new TaskAuthorization();
        var session = tasks.Start("notepad.exe", 42);

        Assert.Equal(32, Convert.FromHexString(session.Token).Length);
        Assert.Same(session, tasks.Validate(session.Token, 42));
        Assert.Throws<UnauthorizedAccessException>(() => tasks.Validate(session.Token, 7));
        Assert.Throws<UnauthorizedAccessException>(() => tasks.Validate("wrong", 42));
    }

    [Fact]
    public void 已有任务不能接受第二个并发授权且结束后令牌失效()
    {
        var tasks = new TaskAuthorization();
        var session = tasks.Start("notepad.exe", 42);
        Assert.Throws<InvalidOperationException>(() => tasks.Start("calc.exe", 7));

        tasks.End(session.Token);

        Assert.Null(tasks.Current);
        Assert.Throws<UnauthorizedAccessException>(() => tasks.Validate(session.Token, 42));
    }

    [Fact]
    public void 本地急停无需令牌即可撤销当前授权()
    {
        var tasks = new TaskAuthorization();
        tasks.Start("notepad.exe", 42);
        tasks.Stop();
        Assert.Null(tasks.Current);
    }

    [Theory]
    [InlineData("删除选中文件", "button", null, true)]
    [InlineData("Send payment", "button", null, true)]
    [InlineData("关闭窗口", "button", "ALT+F4", true)]
    [InlineData("普通保存", "button", "CTRL+S", false)]
    [InlineData("打开文件", "button", null, false)]
    public void 危险动作需要独立的本地确认(string label, string semantic, string? key, bool expected)
    {
        Assert.Equal(expected, RiskAssessment.RequiresConfirmation(label, semantic, key));
    }
}
