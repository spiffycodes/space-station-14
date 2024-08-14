﻿using System.Linq;
using System.Numerics;
using Content.Client.Computer;
using Content.Client.Stylesheets;
using Content.Client.UserInterface.Controls;
using Content.Shared.SensorMonitoring;
using JetBrains.Annotations;
using Robust.Client.AutoGenerated;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.XAML;
using Robust.Shared.Timing;
using ConsoleUIState = Content.Shared.SensorMonitoring.SensorMonitoringConsoleBoundInterfaceState;
using IncrementalUIState = Content.Shared.SensorMonitoring.SensorMonitoringIncrementalUpdate;

namespace Content.Client.SensorMonitoring;

[GenerateTypedNameReferences]
public sealed partial class SensorMonitoringWindow : FancyWindow, IComputerWindow<ConsoleUIState>
{
    [Dependency] private readonly IGameTiming _gameTiming = default!;
    [Dependency] private readonly ILocalizationManager _loc = default!;

    private TimeSpan _retentionTime;
    private readonly Dictionary<int, SensorData> _sensorData = new();

    public SensorMonitoringWindow()
    {
        RobustXamlLoader.Load(this);
    }

    public void UpdateState(ConsoleUIState state)
    {
        _retentionTime = state.RetentionTime;

        _sensorData.Clear();

        foreach (var netSensor in state.Sensors)
        {
            var sensor = new SensorData
            {
                Name = netSensor.Name,
                Address = netSensor.Address,
                DeviceType = netSensor.DeviceType
            };

            _sensorData.Add(netSensor.NetId, sensor);

            foreach (var netStream in netSensor.Streams)
            {
                var stream = new SensorStream
                {
                    Name = netStream.Name,
                    Unit = netStream.Unit
                };

                sensor.Streams.Add(netStream.NetId, stream);

                foreach (var sample in netStream.Samples)
                {
                    stream.Samples.Enqueue(sample);
                }
            }
        }

        Update();
    }

    public void ReceiveMessage(BoundUserInterfaceMessage message)
    {
        if (message is not IncrementalUIState incremental)
            return;

        foreach (var removed in incremental.RemovedSensors)
        {
            _sensorData.Remove(removed);
        }

        foreach (var netSensor in incremental.Sensors)
        {
            // TODO: Fuck this doesn't work if a sensor is added while the UI is open.
            if (!_sensorData.TryGetValue(netSensor.NetId, out var sensor))
                continue;

            foreach (var netStream in netSensor.Streams)
            {
                // TODO: Fuck this doesn't work if a stream is added while the UI is open.
                if (!sensor.Streams.TryGetValue(netStream.NetId, out var stream))
                    continue;

                foreach (var (time, value) in netStream.Samples)
                {
                    stream.Samples.Enqueue(new SensorSample(time + incremental.RelTime, value));
                }
            }
        }

        CullOldSamples();
        Update();
    }

    private void Update()
    {
        Asdf.RemoveAllChildren();

        var curTime = _gameTiming.CurTime;
        var startTime = curTime - _retentionTime;

        foreach (var sensor in _sensorData.Values)
        {
            var labelName = new Label { Text = sensor.Name, StyleClasses = { StyleBase.StyleClassLabelHeading } };
            var labelAddress = new Label
            {
                Text = sensor.Address,
                Margin = new Thickness(4, 0),
                VerticalAlignment = VAlignment.Bottom,
                StyleClasses = { StyleNano.StyleClassLabelSecondaryColor }
            };

            Asdf.AddChild(new BoxContainer
            {
                Orientation = BoxContainer.LayoutOrientation.Horizontal, Children =
                {
                    labelName,
                    labelAddress
                }
            });

            foreach (var stream in sensor.Streams.Values)
            {
                var maxValue = stream.Samples.Max(x => x.Value);

                // TODO: Better way to do this?
                var lastSample = stream.Samples.Last();

                Asdf.AddChild(new BoxContainer
                {
                    Orientation = BoxContainer.LayoutOrientation.Horizontal,
                    Children =
                    {
                        new Label { Text = stream.Name, StyleClasses = { "monospace" }, HorizontalExpand = true },
                        new Label { Text = FormatValue(stream.Unit, lastSample.Value) }
                    }
                });

                Asdf.AddChild(new GraphView(stream.Samples, startTime, curTime, maxValue * 1.1f) { MinHeight = 150 });
                Asdf.AddChild(new PanelContainer { StyleClasses = { StyleBase.ClassLowDivider } });
            }
        }
    }

    private string FormatValue(SensorUnit unit, float value)
    {
        return _loc.GetString(
            "sensor-monitoring-value-display",
            ("unit", unit.ToString()),
            ("value", value));
    }

    private void CullOldSamples()
    {
        var startTime = _gameTiming.CurTime - _retentionTime;

        foreach (var sensor in _sensorData.Values)
        {
            foreach (var stream in sensor.Streams.Values)
            {
                while (stream.Samples.TryPeek(out var sample) && sample.Time < startTime)
                {
                    stream.Samples.Dequeue();
                }
            }
        }
    }

    private sealed class SensorData
    {
        public string Name = "";
        public string Address = "";
        public SensorDeviceType DeviceType;

        public readonly Dictionary<int, SensorStream> Streams = new();
    }

    private sealed class SensorStream
    {
        public string Name = "";
        public SensorUnit Unit;
        public readonly Queue<SensorSample> Samples = new();
    }

    private sealed class GraphView : Control
    {
        private readonly Queue<SensorSample> _samples;
        private readonly TimeSpan _startTime;
        private readonly TimeSpan _curTime;
        private readonly float _maxY;

        public GraphView(Queue<SensorSample> samples, TimeSpan startTime, TimeSpan curTime, float maxY)
        {
            _samples = samples;
            _startTime = startTime;
            _curTime = curTime;
            _maxY = maxY;
            RectClipContent = true;
        }

        protected override void Draw(DrawingHandleScreen handle)
        {
            base.Draw(handle);

            var window = (float) (_curTime - _startTime).TotalSeconds;

            // TODO: omg this is terrible don't fucking hardcode this size to something uncached huge omfg.
            var vertices = new Vector2[25000];
            var countVtx = 0;

            var lastPoint = new Vector2(float.NaN, float.NaN);

            foreach (var (time, sample) in _samples)
            {
                var relTime = (float) (time - _startTime).TotalSeconds;

                var posY = PixelHeight - (sample / _maxY) * PixelHeight;
                var posX = (relTime / window) * PixelWidth;

                var newPoint = new Vector2(posX, posY);

                if (float.IsFinite(lastPoint.X))
                {
                    handle.DrawLine(lastPoint, newPoint, Color.White);

                    vertices[countVtx++] = lastPoint;
                    vertices[countVtx++] = lastPoint with { Y = PixelHeight };
                    vertices[countVtx++] = newPoint;
                    vertices[countVtx++] = newPoint;
                    vertices[countVtx++] = lastPoint with { Y = PixelHeight };
                    vertices[countVtx++] = newPoint with { Y = PixelHeight };
                }

                lastPoint = newPoint;
            }

            handle.DrawPrimitives(DrawPrimitiveTopology.TriangleList, vertices.AsSpan(0, countVtx), Color.White.WithAlpha(0.1f));
        }
    }
}

[UsedImplicitly]
public sealed class
    SensorMonitoringConsoleBoundUserInterface : ComputerBoundUserInterface<SensorMonitoringWindow, ConsoleUIState>
{
    public SensorMonitoringConsoleBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }
}
