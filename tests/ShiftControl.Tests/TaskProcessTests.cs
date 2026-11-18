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
    public void Same_name_processes_become_one_group_with_summed_usage()
    {
        TaskProcess[] items =
        [
            new(10,"chrome.exe",300,5,100,SessionId:1),
            new(11,"Chrome.exe",100,20,300,SessionId:1,HasWindow:true),
            new(12,"chrome.exe",200,null,null,SessionId:1),
            new(20,"notepad.exe",400,40,50,SessionId:1,HasWindow:true),
        ];
        var groups = TaskProcesses.QueryGroups(items,null,ProcessSort.Cpu,false);
        Assert.Equal(["notepad.exe","chrome.exe"],groups.Select(g=>g.Name.ToLowerInvariant())); // por CPU: 40% antes dos 25% somados
        var chrome = groups[1];
        Assert.Equal(3,chrome.Count);
        Assert.Equal([11,12,10],chrome.Members.Select(m=>m.Pid));      // mais antigo primeiro
        var row = chrome.Summary();
        Assert.Equal(25,row.CpuPercent);
        Assert.Equal(400,row.MemoryBytes);
        Assert.True(row.HasWindow);
        Assert.Equal(11,row.Pid);                                       // identidade do membro com janela
        Assert.Equal("notepad.exe",Assert.Single(groups,g=>g.Count==1).Name);
        Assert.Equal("chrome.exe",Assert.Single(TaskProcesses.QueryGroups(items,"12",ProcessSort.Name,false)).Name,ignoreCase:true); // PID de um membro
        Assert.Equal(2,TaskProcesses.QueryGroups(items,null,ProcessSort.Name,true).Count);
        Assert.Equal(["chrome.exe","notepad.exe"],TaskProcesses.QueryGroups(items,null,ProcessSort.Name,false).Select(g=>g.Name.ToLowerInvariant()));
    }

    [Fact]
    public void Ending_a_group_goes_oldest_first_skips_protected_and_counts_each_process_once()
    {
        TaskProcess[] items =
        [
            new(10,"chrome.exe",300,SessionId:1),
            new(11,"chrome.exe",100,SessionId:1),
            new(12,"chrome.exe",200,SessionId:0),   // sessão de serviço: protegido
            new(20,"notepad.exe",50,SessionId:1),   // outro nome: nunca tocado
        ];
        var chamados = new List<int>();
        var first = TaskProcesses.EndAll("CHROME.EXE",items,p => { chamados.Add(p.Pid); return p.Pid == 10 ? EndProcessResult.Ending : EndProcessResult.Ended; });
        Assert.Equal([11,10],chamados);
        Assert.Equal(EndProcessResult.Protected,first.Single(r=>r.Item.Pid==12).Result);
        // segunda passada: o 10 ainda estava saindo e aparece de novo; conta uma vez só
        var second = TaskProcesses.EndAll("chrome.exe",[items[0],items[2]],_ => EndProcessResult.AlreadyExited);
        var summary = TaskProcesses.Summarize("chrome.exe",first.Concat(second));
        Assert.Equal(2,summary.Ended);
        Assert.Equal(1,summary.Blocked);
        Assert.Equal(3,summary.Total);
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
    public async Task Ending_a_group_closes_every_process_with_that_name()
    {
        // cópia do ping com nome único: o grupo não pode pegar nenhum outro processo da máquina
        string pasta = Path.Combine(Path.GetTempPath(), "shiftcontrol-grupo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(pasta);
        string exe = Path.Combine(pasta, "sc-grupo-teste.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), exe);
        var filhos = Enumerable.Range(0, 3).Select(_ => Process.Start(new ProcessStartInfo(exe)
        {
            ArgumentList = { "-n", "60", "127.0.0.1" }, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
        })!).ToList();
        try
        {
            var amostra = new ProcessSampler().Sample(TestContext.Current.CancellationToken);
            var grupo = Assert.Single(TaskProcesses.QueryGroups(amostra, "sc-grupo-teste", ProcessSort.Name, false));
            Assert.Equal(3, grupo.Count);
            var resumo = TaskProcesses.Summarize(grupo.Name, TaskProcesses.EndAll(grupo.Name, amostra));
            Assert.Equal(3, resumo.Ended);
            Assert.Equal(0, resumo.Blocked);
            foreach (var f in filhos)
                await f.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally
        {
            foreach (var f in filhos) { if (!f.HasExited) f.Kill(); f.Dispose(); }
            try { Directory.Delete(pasta, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
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
