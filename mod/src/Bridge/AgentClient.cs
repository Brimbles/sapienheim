using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ValheimCompanion.Bridge
{
    /// <summary>
    /// Server only: TCP connection to the companion agent, carrying one JSON object per line.
    /// Socket I/O runs on background threads; incoming messages are handled on the Unity main thread.
    /// Reconnects with exponential backoff. Messages sent while disconnected are dropped.
    /// </summary>
    internal class AgentClient : MonoBehaviour
    {
        private const int ConnectTimeoutMs = 5000;
        private const int MaxBackoffMs = 30000;

        private static AgentClient s_instance;

        private readonly ConcurrentQueue<JObject> _inbox = new ConcurrentQueue<JObject>();
        private BlockingCollection<string> _outbox = new BlockingCollection<string>();
        private Thread _thread;
        private volatile bool _running;
        private volatile bool _connected;
        private TcpClient _client;

        // Captured on the main thread when the connection loop starts.
        private string _host;
        private int _port;
        private string _token;
        private string _world;

        public static bool IsConnected => s_instance && s_instance._connected;

        private void Awake() => s_instance = this;

        public static bool Send(JObject msg)
        {
            if (!IsConnected)
            {
                return false;
            }
            s_instance._outbox.Add(msg.ToString(Formatting.None));
            return true;
        }

        public static bool SendEvent(string name, JObject data) =>
            Send(new JObject { ["type"] = "event", ["name"] = name, ["data"] = data });

        private void Update()
        {
            bool shouldRun = Role.IsServer && ZNet.instance && Plugin.AgentToken.Value.Length > 0;
            if (shouldRun && !_running)
            {
                StartLoop();
            }
            else if (!shouldRun && _running)
            {
                StopLoop();
            }

            while (_inbox.TryDequeue(out JObject msg))
            {
                try
                {
                    CommandHandler.Handle(msg);
                }
                catch (Exception e)
                {
                    Jotunn.Logger.LogError($"Agent message failed: {e}");
                }
            }
        }

        private void OnDestroy() => StopLoop();

        private void StartLoop()
        {
            _host = Plugin.AgentHost.Value;
            _port = Plugin.AgentPort.Value;
            _token = Plugin.AgentToken.Value;
            _world = ZNet.instance.GetWorldName();
            _running = true;
            _thread = new Thread(ConnectionLoop) { IsBackground = true, Name = "CompanionAgentClient" };
            _thread.Start();
        }

        private void StopLoop()
        {
            _running = false;
            _connected = false;
            try { _client?.Close(); } catch { /* already closed */ }
            _thread = null;
        }

        private void ConnectionLoop()
        {
            int backoffMs = 1000;
            int failures = 0;
            while (_running)
            {
                try
                {
                    RunConnection();
                    backoffMs = 1000; // it connected; start over after a clean disconnect
                    failures = 0;
                }
                catch (Exception e) when (_running)
                {
                    // Log the first failure and then every 10th, so a stopped agent doesn't flood the log.
                    if (failures++ % 10 == 0)
                    {
                        Jotunn.Logger.LogWarning($"Agent connection to {_host}:{_port} failed ({failures}x): {e.Message}");
                    }
                }
                _connected = false;
                if (!_running)
                {
                    break;
                }
                Thread.Sleep(backoffMs);
                backoffMs = Math.Min(backoffMs * 2, MaxBackoffMs);
            }
        }

        private void RunConnection()
        {
            using (var client = new TcpClient())
            {
                _client = client;
                IAsyncResult connect = client.BeginConnect(_host, _port, null, null);
                if (!connect.AsyncWaitHandle.WaitOne(ConnectTimeoutMs))
                {
                    throw new TimeoutException("connect timed out");
                }
                client.EndConnect(connect);
                client.NoDelay = true;

                NetworkStream stream = client.GetStream();
                var reader = new StreamReader(stream, new UTF8Encoding(false));
                var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };

                var hello = new JObject
                {
                    ["type"] = "hello",
                    ["token"] = _token,
                    ["mod_version"] = Plugin.PluginVersion,
                    ["world"] = _world,
                };
                writer.WriteLine(hello.ToString(Formatting.None));

                JObject ack = ReadMessage(reader);
                if (ack == null || (string)ack["type"] != "hello_ack")
                {
                    throw new IOException("handshake rejected (check Agent.Token)");
                }
                Jotunn.Logger.LogInfo($"Connected to agent {ack["agent_version"]} at {_host}:{_port}");

                // Fresh outbox per connection so stale messages are never sent.
                _outbox = new BlockingCollection<string>();
                BlockingCollection<string> outbox = _outbox;
                _connected = true;

                var writerThread = new Thread(() => WriteLoop(writer, outbox)) { IsBackground = true, Name = "CompanionAgentWriter" };
                writerThread.Start();
                try
                {
                    JObject msg;
                    while (_running && (msg = ReadMessage(reader)) != null)
                    {
                        _inbox.Enqueue(msg);
                    }
                }
                finally
                {
                    _connected = false;
                    outbox.CompleteAdding();
                }
                if (_running)
                {
                    Jotunn.Logger.LogWarning("Agent disconnected");
                }
            }
        }

        private static void WriteLoop(StreamWriter writer, BlockingCollection<string> outbox)
        {
            try
            {
                foreach (string line in outbox.GetConsumingEnumerable())
                {
                    writer.WriteLine(line);
                }
            }
            catch (Exception)
            {
                // Socket closed; the read loop notices and reconnects.
            }
        }

        private static JObject ReadMessage(StreamReader reader)
        {
            while (true)
            {
                string line = reader.ReadLine();
                if (line == null)
                {
                    return null;
                }
                if (line.Trim().Length == 0)
                {
                    continue;
                }
                try
                {
                    return JObject.Parse(line);
                }
                catch (JsonException)
                {
                    Jotunn.Logger.LogWarning($"Agent sent invalid JSON: {line}");
                }
            }
        }
    }
}
