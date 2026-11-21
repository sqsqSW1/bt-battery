namespace BtBattery.App;

/// <summary>
/// 把三层枚举合成一次快照。连接和断开靠设备变化消息立刻重扫，电量再按设置里的间隔补一次。
/// </summary>
internal sealed class DeviceMonitor : IDisposable
{
    private readonly SynchronizationContext _ui;
    private readonly Func<AppSettings> _settings;
    private readonly WinRtBluetoothScanner _winRt = new();
    private readonly BatteryEnricher _batteries = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _wakeLock = new();
    private string _signature = "";

    public DeviceMonitor(SynchronizationContext ui, Func<AppSettings> settings)
    {
        _ui = ui;
        _settings = settings;
    }

    public event EventHandler<MonitorSnapshot>? Updated;

    public void Start()
    {
        _winRt.Start(Poke);
        Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Poke()
    {
        try
        {
            lock (_wakeLock)
            {
                if (_cts.IsCancellationRequested || _wake.CurrentCount > 0)
                    return;
                _wake.Release();
            }
        }
        catch (ObjectDisposedException)
        {
            // 退出时监视回调可能还在响。
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        Poke();
        _winRt.Dispose();
        _cts.Dispose();
        _wake.Dispose();
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ScanAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                AppLog.Error("扫描失败。", ex);
                Publish(new MonitorSnapshot(Array.Empty<TrackedDevice>(), "读取蓝牙设备时出错，详细原因写在日志里。"));
            }

            var interval = TimeSpan.FromSeconds(AppSettings.NormalizePollInterval(_settings().PollIntervalSeconds));
            bool poked;
            try
            {
                poked = await _wake.WaitAsync(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (!poked)
                continue;

            try
            {
                await Task.Delay(700, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            while (_wake.CurrentCount > 0 && _wake.Wait(0))
            {
                // 把一小段时间里的多次插拔合成一轮扫描。
            }
        }
    }

    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        var observations = new List<DeviceObservation>();
        var warnings = new List<string>();

        try
        {
            var classic = ClassicBluetoothScanner.Read();
            observations.AddRange(classic.Observations);
            warnings.AddRange(classic.Warnings);
        }
        catch (Exception ex)
        {
            warnings.Add("经典蓝牙接口调用失败。");
            AppLog.Error("经典蓝牙接口调用失败。", ex);
        }

        var winRt = await _winRt.ReadAsync(cancellationToken).ConfigureAwait(false);
        observations.AddRange(winRt.Observations.Where(item => !string.IsNullOrEmpty(item.Address)));
        warnings.AddRange(winRt.Warnings);

        try
        {
            var pnp = PnpBluetoothScanner.Read();
            observations.AddRange(pnp.Observations);
            warnings.AddRange(pnp.Warnings);
        }
        catch (Exception ex)
        {
            warnings.Add("即插即用枚举失败。");
            AppLog.Error("即插即用枚举失败。", ex);
        }

        await _batteries.AddPowerReadingsAsync(observations, cancellationToken).ConfigureAwait(false);

        var preliminary = DeviceMerger.Merge(observations);
        await _batteries.AddGattReadingsAsync(preliminary, observations, cancellationToken).ConfigureAwait(false);
        var merged = DeviceMerger.Merge(observations);

        if (merged.Count == 0)
        {
            var radio = await WinRtBluetoothScanner.RadioWarningAsync(cancellationToken).ConfigureAwait(false);
            if (radio is { Length: > 0 })
                warnings.Insert(0, radio);
        }

        var explained = false;
        foreach (var device in merged.Where(item => item.IsConnected && DeviceKindLabels.IsAccessory(item.Kind) && !item.Battery.IsKnown))
        {
            if (device.Notes.Any(note => note.Contains("电量", StringComparison.Ordinal) || note.Contains("GATT", StringComparison.Ordinal) || note.Contains("电池", StringComparison.Ordinal)))
                continue;

            observations.Add(new DeviceObservation
            {
                Address = device.Address,
                Source = ObservationSource.Pnp,
                Note = "系统蓝牙电量属性、Windows 电池接口和 GATT 都没有给出百分比",
            });
            explained = true;
        }

        if (explained)
            merged = DeviceMerger.Merge(observations);

        var warning = warnings.Count == 0 ? null : string.Join(" ", warnings.Distinct());
        var snapshot = new MonitorSnapshot(merged, warning);
        var signature = Signature(snapshot);
        if (!string.Equals(signature, _signature, StringComparison.Ordinal))
        {
            _signature = signature;
            AppLog.Info($"已连接 {merged.Count} 台蓝牙设备。{warning}");
        }

        Publish(snapshot);
    }

    private void Publish(MonitorSnapshot snapshot)
    {
        _ui.Post(_ => Updated?.Invoke(this, snapshot), null);
    }

    private static string Signature(MonitorSnapshot snapshot)
    {
        var body = string.Join("|", snapshot.Devices.Select(device =>
            $"{device.Address}:{device.Kind}:{device.Battery.Percent}:{device.Name}"));
        return body + "#" + snapshot.Warning;
    }
}
