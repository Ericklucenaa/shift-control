using System.Diagnostics;
using ShiftControl.Maintenance;

namespace ShiftControl.Tests;

public class TaskProcessTests
{
    [Theory]
    [InlineData(2, 1, 4, 50)]
    [InlineData(8, 2, 4, 100)]
    [InlineData(0, 2, 8, 0)]
    [InlineData(99, 1, 1, 100)]
    public void Cpu_is_normalized_to_total_capacity(double cpu, double elapsed, int cores, double expected) =>
        Assert.Equal(expected, ProcessSampler.CpuPercent(cpu, elapsed, cores));

    [Theory]
    [InlineData(-1, 1, 4)]
    [InlineData(1, 0, 4)]
    [InlineData(1, 1, 0)]
    [InlineData(double.NaN, 1, 4)]
    public void Unavailable_cpu_is_not_presented_as_zero(double cpu, double elapsed, int cores) => Assert.Null(ProcessSampler.CpuPercent(cpu, elapsed, cores));

    [Fact]
    public void Search_filter_and_order_keep_missing_measurements_last()
    {
        TaskProcess[] items =
        [
            new(20,"beta.exe",100,25,2048,SessionId:1),
            new(30,"Alpha.exe",200,null,4096,SessionId:1,HasWindow:true),
            new(40,"gamma.exe",300,50,null,SessionId:1),
        ];
        Assert.Equal([40,20,30],TaskProcesses.Query(items,null,ProcessSort.Cpu,false).Select(p=>p.Pid));
        Assert.Equal([30,20,40],TaskProcesses.Query(items,null,ProcessSort.Memory,false).Select(p=>p.Pid));
        Assert.Equal(30,Assert.Single(TaskProcesses.Query(items,"ALPHA",ProcessSort.Name,false)).Pid);
        Assert.Equal(20,Assert.Single(TaskProcesses.Query(items,"20",ProcessSort.Name,false)).Pid);
        Assert.Equal(30,Assert.Single(TaskProcesses.Query(items,null,ProcessSort.Name,true)).Pid);
        Assert.Equal([20,30,40],TaskProcesses.Query(items,null,ProcessSort.Pid,false).Select(p=>p.Pid));
    }

    [Fact]
    public void Critical_names_services_and_app_itself_are_blocked_before_opening_handles()
    {
        foreach (var name in new[] { "csrss.exe", "LSASS.EXE", "explorer.exe", "ShiftControl.exe" })
            Assert.Equal(EndProcessResult.Protected,TaskProcesses.End(new(99999,name,10,SessionId:1)));
        Assert.Equal(EndProcessResult.Protected,TaskProcesses.End(new(Environment.ProcessId,"any.exe",10,SessionId:1)));
        Assert.False(new TaskProcess(123,"app.exe",null,SessionId:1).CanEnd);
        Assert.False(new TaskProcess(123,"app.exe",10,SessionId:0).CanEnd);
        Assert.False(new TaskProcess(123,"app.exe",10).CanEnd);
    }
}

[Trait("Category","Integration")]
public class TaskProcessIntegrationTests
{
    [Fact]
    public async Task Real_sampling_reads_memory_and_measures_cpu_after_first_sample()
    {
        var sampler = new ProcessSampler();
        var first = sampler.Sample(TestContext.Current.CancellationToken);
        var own = Assert.Single(first,p=>p.Pid==Environment.ProcessId);
        Assert.True(own.MemoryBytes > 0);
        Assert.True(own.CreationFileTime > 0);
        Assert.Null(own.CpuPercent);
        Assert.False(own.CanEnd);
        var details = TaskProcesses.Details(own);
        Assert.NotNull(details);
        Assert.True(File.Exists(details.Path));
        await Task.Delay(150,TestContext.Current.CancellationToken);
        var second = sampler.Sample(TestContext.Current.CancellationToken);
        Assert.NotNull(Assert.Single(second,p=>p.Pid==Environment.ProcessId).CpuPercent);
        Assert.All(second.Where(p=>p.CpuPercent.HasValue),p=>Assert.InRange(p.CpuPercent!.Value,0,100));
        sampler.Reset();
        Assert.All(sampler.Sample(TestContext.Current.CancellationToken),p=>Assert.Null(p.CpuPercent));
    }

    [Fact]
    public async Task Termination_rejects_stale_identity_and_only_ends_the_test_owned_child()
    {
        using var child = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"ping.exe"))
        {
            ArgumentList = { "-n", "60", "127.0.0.1" },
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
        })!;
        try
        {
            var item = Assert.Single(new ProcessSampler().Sample(TestContext.Current.CancellationToken),p=>p.Pid==child.Id);
            Assert.True(item.CanEnd);
            Assert.Equal(EndProcessResult.IdentityChanged,TaskProcesses.End(item with { CreationFileTime = item.CreationFileTime + 1 }));
            Assert.False(child.HasExited);
            Assert.Contains(TaskProcesses.End(item),new[] { EndProcessResult.Ended, EndProcessResult.Ending });
            await child.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5),TestContext.Current.CancellationToken);
            Assert.Null(TaskProcesses.Details(item));
        }
        finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(TestContext.Current.CancellationToken); } }
    }
}
