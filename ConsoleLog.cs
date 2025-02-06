using System;
using System.Collections.Generic;
using System.Numerics;
using ExileCore2.Shared;
using ImGuiNET;

namespace PluginUpdater
{
    public class LogEntry(string message, Vector4 color)
    {
        public string Message { get; } = message;
        public DateTime Timestamp { get; } = DateTime.Now;
        public Vector4 Color { get; } = color;
    }

    public class ConsoleLog
    {
        private readonly List<LogEntry> _logEntries = [];
        private readonly object _logLock = new();

        public static readonly Vector4
            ColorInfo = new(1.0f, 1.0f, 1.0f, 1.0f),      // White
            ColorWarning = new(1.0f, 0.8f, 0.0f, 1.0f),   // Yellow
            ColorError = new(1.0f, 0.2f, 0.2f, 1.0f),     // Red
            ColorSuccess = new(0.2f, 1.0f, 0.2f, 1.0f);   // Green

        public void AddLogMessage(string message, Vector4 color)
        {
            lock (_logLock)
            {
                _logEntries.Add(new LogEntry(message, color));
            }
        }

        public void AddNotificationMessage(string id, string message, Vector4 color)
        {
            lock (_logLock)
            {
                _logEntries.Add(new LogEntry(message, color));
            }

            PluginUpdater.Instance.PostNotification(new PluginNotification("", id, message));
        }

        public void LogInfo(string message) =>
            AddLogMessage(message, ColorInfo);

        public void LogWarning(string message) =>
            AddLogMessage(message, ColorWarning);

        public void LogError(string message) =>
            AddLogMessage(message, ColorError);

        public void LogSuccess(string message) =>
            AddLogMessage(message, ColorSuccess);

        public void RenderConsoleLog()
        {
            ImGui.Spacing();
            ImGui.Text("Console Log");

            var size = new Vector2(-1, -1);
            ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.2f, 0.2f, 0.2f, 1.0f));
            if (ImGui.BeginChild("##consolelog", size, ImGuiChildFlags.Border,
                    PluginUpdater.Instance.Settings.WrapLogMessages
                        ? ImGuiWindowFlags.None
                        : ImGuiWindowFlags.HorizontalScrollbar))
            {
                lock (_logLock)
                {
                    foreach (var entry in _logEntries)
                    {
                        ImGui.PushStyleColor(ImGuiCol.Text, entry.Color);
                        ImGui.TextWrapped($"[{entry.Timestamp:HH:mm:ss}] {entry.Message}");
                        ImGui.PopStyleColor();
                    }
                }

                if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY())
                {
                    ImGui.SetScrollHereY(1.0f);
                }
            }
            ImGui.EndChild();
            ImGui.PopStyleColor();
        }
    }
}