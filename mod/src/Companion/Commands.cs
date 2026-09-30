using Jotunn.Entities;
using Jotunn.Managers;
using ValheimCompanion.Net;

namespace ValheimCompanion.Companion
{
    internal static class Commands
    {
        public static void Register()
        {
            CommandManager.Instance.AddConsoleCommand(new SpawnCommand());
            CommandManager.Instance.AddConsoleCommand(new DespawnCommand());
            CommandManager.Instance.AddConsoleCommand(new KillCommand());
        }

        private class SpawnCommand : ConsoleCommand
        {
            public override string Name => "cmp_spawn";
            public override string Help => "Spawn your companion in front of you (admin only)";

            public override void Run(string[] args)
            {
                Player player = Player.m_localPlayer;
                if (!player)
                {
                    Console.instance.Print("You need to be in a world.");
                    return;
                }
                var pos = player.transform.position + player.transform.forward * 2f + UnityEngine.Vector3.up * 0.5f;
                Rpcs.SendSpawnRequest(pos, player.GetPlayerID(), player.GetPlayerName());
            }
        }

        private class DespawnCommand : ConsoleCommand
        {
            public override string Name => "cmp_despawn";
            public override string Help => "Remove the companion from the world (admin only)";

            public override void Run(string[] args) => Rpcs.SendDespawnRequest();
        }

        private class KillCommand : ConsoleCommand
        {
            public override string Name => "cmp_kill";
            public override string Help => "Debug: kill the companion to test bouncing back (admin only)";

            public override void Run(string[] args) => Rpcs.SendKillRequest();
        }
    }
}
