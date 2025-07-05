﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text.Json;
using System.Threading;
using PCL.Core.Helper;
using PCL.Core.LifecycleManagement;

namespace PCL.Core.Service;

[LifecycleService(LifecycleState.BeforeLoading, Priority = -10)]
public sealed class PromoteService : ILifecycleService
{
    public string Identifier => "promote";
    public string Name => "提权服务";
    public bool SupportAsyncStart => false;

    private static LifecycleContext? _context;
    private PromoteService() { _context = Lifecycle.GetContext(this); }
    private static LifecycleContext Context => _context!;
    
    private static Process? _promoteProcess;
    private static NamedPipeServerStream? _promotePipeServer;
    
    private static readonly LinkedList<PromoteOperation> PendingOperations = [];
    
    private record PromoteOperation(string Command, Action<string>? Callback, bool DetailLog);
    
    /// <summary>
    /// 提权进程是否正在运行。
    /// </summary>
    public static bool IsPromoteProcessRunning => _promoteProcess != null;
    
    /// <summary>
    /// 当前进程是否是提权进程。
    /// </summary>
    public static bool IsCurrentProcessPromoted { get; private set; }
    
    private static string GetPromotePipeName(int processId) => $"PCLCE_PM@{processId}";

    private static readonly Dictionary<string, Func<string?, string?>> OperationFunctions = new();

    /// <summary>
    /// 添加提权操作，仅在提权进程中有效。
    /// </summary>
    /// <param name="name">操作名</param>
    /// <param name="operation">操作实现，接收参数并返回结果，返回值会被自动压缩为单行</param>
    /// <returns>是否添加成功，若在主进程中调用或已存在相同操作名，则为 <c>false</c></returns>
    public static bool AddOperationFunction(string name, Func<string?, string?> operation)
    {
        if (!IsCurrentProcessPromoted || OperationFunctions.ContainsKey(name)) return false;
        OperationFunctions[name] = operation;
        return true;
    }

    /// <summary>
    /// 添加自动将参数 JSON 反序列化的提权操作，仅在提权进程中有效。
    /// </summary>
    /// <param name="name">操作名</param>
    /// <param name="operation">操作实现，接收反序列化的并返回结果，返回值会被自动压缩为单行</param>
    /// <typeparam name="TValue">反序列化的目标类型</typeparam>
    /// <returns>是否添加成功，若在主进程中调用或已存在相同操作名，则为 <c>false</c></returns>
    public static bool AddJsonOperationFunction<TValue>(string name, Func<TValue?, string?> operation)
    {
        return AddOperationFunction(name, arg =>
        {
            if (arg == null) return OperationErrEmpty;
            var obj = JsonSerializer.Deserialize<TValue>(arg);
            return operation(obj);
        });
    }
    
    private const string OperationErrNotFound = "ERR_OPERATION_NOT_FOUND";
    private const string OperationErrInvalidArgument = "ERR_ILLEGAL_ARGUMENT";
    private const string OperationErrExceptionThrown = "ERR_UNHANDLED_EXCEPTION";
    private const string OperationErrEmpty = "EMPTY";
    
    /// <summary>
    /// 提权进程接收到操作请求时触发的事件，接收一个字符串作为操作命令并返回一个字符串作为结果。<br/>
    /// <b>注意：如果你不知道这是做什么的，请勿覆盖默认实现。</b>请使用 <see cref="AddOperationFunction"/>。
    /// </summary>
    public static Func<string, string> Operate { private get; set; } = command =>
    {
        var split = command.Split([' '], 2);
        OperationFunctions.TryGetValue(split[0], out var operation);
        if (operation == null) return OperationErrNotFound;
        try
        {
            return operation(split.Length > 1 ? split[1] : null) ?? OperationErrEmpty;
        }
        catch (Exception ex)
        {
            Context.Warn("操作出错", ex);
            return OperationErrExceptionThrown;
        }
    };
    
    private static string ShortenString(string str)
    {
#if DEBUG || DEBUGCI
        const int maxLength = 40;
#else
        const int maxLength = 15;
#endif
        if (str.Length <= maxLength) return str;
        return str[..maxLength] + "...";
    }
    
    // 提权进程: 连接管道开始通信
    private static void PerformAsPromoteProcess(string pid)
    {
        Context.Info("正在连接提权通信管道");
        var process = Process.GetProcessById(int.Parse(pid));
        // 验证来源
        var mainProcessPath = Path.GetFullPath(process.MainModule!.FileName);
        if (!string.Equals(mainProcessPath, NativeInterop.ExecutablePath, StringComparison.OrdinalIgnoreCase))
        {
            Context.Error("来源验证失败，正在退出");
            return;
        }
        // 连接管道
        var pipeName = GetPromotePipeName(process.Id);
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
        pipe.Connect(10000);
        Context.Info("已连接，开始通信");
        var reader = new StreamReader(pipe);
        var writer = new StreamWriter(pipe);
        while (true)
        {
            var command = reader.ReadLine();
            if (string.IsNullOrEmpty(command))
            {
                Context.Info("管道已关闭，正在退出");
                break;
            }
            Context.Debug($"正在执行: {ShortenString(command)}");
            var result = Operate(command) ?? OperationErrEmpty;
            Context.Trace($"返回结果: {ShortenString(result)}");
            writer.WriteLine(result.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' '));
            writer.Flush();
            Context.Trace("返回成功");
        }
    }
    
    private static readonly AutoResetEvent ActivateEvent = new(false);

    // 主进程: 管道连接回调
    private static bool PromotePipeCallback(StreamReader reader, StreamWriter writer, Process? client)
    {
        while (IsPromoteProcessRunning) lock (PendingOperations)
        {
            ActivateEvent.WaitOne();
            foreach (var operation in PendingOperations.ToArray())
            {
                var command = operation.Command.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
                var detail = operation.DetailLog ? command : ShortenString(command);
                Context.Debug($"正在执行: {detail}");
                writer.WriteLine(command);
                writer.Flush();
                var result = reader.ReadLine();
                if (result == null)
                {
                    Context.Warn("管道输入流已结束");
                    break;
                }
                Context.Trace($"执行结果: {result}");
                operation.Callback?.Invoke(result);
                PendingOperations.RemoveFirst();
            }
        }
        return false;
    }

    // 主进程: 初始化提权后台服务
    private static bool StartPromoteProcess()
    {
        // 启动提权进程
        _promoteProcess = NativeInterop.Start(
            NativeInterop.ExecutablePath, $"promote {NativeInterop.CurrentProcessId}", true);
        if (_promoteProcess == null)
        {
            Context.Warn("提权进程启动失败");
            return false;
        }
        _promoteProcess.Exited += (_, _) => _promoteProcess = null;
        // 启动提权通信管道服务端
        _promotePipeServer ??= NativeInterop.StartPipeServer(
            "Promote", GetPromotePipeName(NativeInterop.CurrentProcessId), PromotePipeCallback,
            () => _promotePipeServer = null, true, [_promoteProcess.Id]);
        return true;
    }

    /// <summary>
    /// 向等待区添加操作。
    /// </summary>
    /// <param name="command">操作命令</param>
    /// <param name="callback">结果返回后的回调</param>
    /// <param name="detailLog">指定是否打印详细日志，若为 <c>false</c>，则日志仅保留前 40 或 15 字符（取决于是否为调试构建）</param>
    public static void AppendOperation(string command, Action<string>? callback = null, bool detailLog = true)
    {
        lock (PendingOperations)
        {
            PendingOperations.AddLast(new PromoteOperation(command, callback, detailLog));
        }
    }

    /// <summary>
    /// 开始执行操作。
    /// </summary>
    /// <returns>是否成功开始执行</returns>
    public static bool Activate()
    {
        if (!IsPromoteProcessRunning && !StartPromoteProcess()) return false;
        ActivateEvent.Set();
        return true;
    }

    // name: start
    // arg: path\to\executable[.] ; argument
    private static string? _StartProcess(string? arg)
    {
        if (arg == null) return OperationErrInvalidArgument;
        var split = arg.Split([" ; "], 2, StringSplitOptions.RemoveEmptyEntries);
        var createNoWindow = false;
        if (split[0].EndsWith("."))
        {
            split[0] = split[0][..^1];
            createNoWindow = true;
        }
        var psi = new ProcessStartInfo(split[0]);
        if (createNoWindow) psi.CreateNoWindow = true;
        if (split.Length > 1) psi.Arguments = split[1];
        return _StartProcessWithInfo(psi);
    }

    // name: start-json
    // arg: {...}
    private static string? _StartProcessWithInfo(ProcessStartInfo? info)
    {
        if (info == null) return OperationErrInvalidArgument;
        var process = Process.Start(info);
        return process?.Id.ToString();
    }
    
    public void Start()
    {
        var args = Environment.GetCommandLineArgs();
        if (args is [_, "promote", _])
        {
            Context.Info("当前进程为提权进程");
            IsCurrentProcessPromoted = true;
            // 预定义操作
            AddOperationFunction("start", _StartProcess);
            AddJsonOperationFunction<ProcessStartInfo>("start-json", _StartProcessWithInfo);
            // 结束生命周期管理，启动提权操作线程
            Lifecycle.PendingLogFileName = "LastPending_Promote.log";
            new Thread(() => PerformAsPromoteProcess(args[2])) { Name = "Promote" }.Start();
            Context.RequestStopLoading();
            Context.DeclareStopped();
        }
        else
        {
            Context.Info("当前进程为主进程");
            IsCurrentProcessPromoted = false;
            // TODO 提权进程自动启动
        }
    }

    public void Stop()
    {
        if (_promotePipeServer != null)
        {
            Context.Debug("正在结束提权管道服务");
            _promotePipeServer.Dispose();
        }
        if (_promoteProcess != null && !_promoteProcess.WaitForExit(3000))
        {
            Context.Debug("正在结束提权进程");
            NativeInterop.Kill(_promoteProcess, 0, true);
        }
    }
}
