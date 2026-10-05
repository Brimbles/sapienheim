using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.Networking;

namespace ValheimCompanion.Companion
{
    /// <summary>
    /// The companion's voice clips: .ogg or .wav files in the <c>sounds</c> folder next to the mod's DLL (so they ship
    /// with the mod and every player has them). A clip is named by its file name, e.g. <c>laugh.ogg</c> is "laugh".
    /// <list type="bullet">
    /// <item><b>Moments</b>: the server plays one at fixed moments (<see cref="Moments"/>): a clip named after the moment,
    /// or with a suffix (battle_cry, battle_cry2, battle_cry_angry...), chosen at random when there are several.</item>
    /// <item><b>With speech</b>: the agent sees the clip names and can add one to a <c>say</c>.</item>
    /// </list>
    /// The server broadcasts the clip's name; each client plays it in 3D from the companion through the game's sound
    /// effects mixer, so it follows the player's volume setting.
    /// </summary>
    internal static class CompanionSounds
    {
        /// <summary>Moments the mod plays a clip for, if one exists.</summary>
        public static readonly string[] Moments = { "battle_cry", "victory", "timber", "door", "death", "level_up", "greeting", "respawn" };

        private const float MinGap = 2f; // between clips from the server, so they never pile up

        private static readonly Dictionary<string, AudioClip> s_clips = new Dictionary<string, AudioClip>();
        private static List<string> s_names;
        private static Dictionary<string, string> s_captions;
        private static float s_lastPlayed = -MinGap;
        private static AudioMixerGroup s_sfxGroup;
        private static bool s_sfxLooked;

        private static string Folder => Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".", "sounds");

        /// <summary>The clip names available (from the files, so the headless server knows them without loading audio).</summary>
        public static List<string> Names
        {
            get
            {
                if (s_names == null)
                {
                    s_names = Directory.Exists(Folder)
                        ? Directory.GetFiles(Folder).Where(IsClip).Select(f => Path.GetFileNameWithoutExtension(f).ToLowerInvariant()).Distinct().OrderBy(n => n).ToList()
                        : new List<string>();
                }
                return s_names;
            }
        }

        /// <summary>What a clip says (from sounds/captions.json, written by make_clips.py), or null.</summary>
        public static string Caption(string clip)
        {
            if (s_captions == null)
            {
                s_captions = new Dictionary<string, string>();
                string file = Path.Combine(Folder, "captions.json");
                if (File.Exists(file))
                {
                    try
                    {
                        foreach (var kv in Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(file)))
                        {
                            s_captions[kv.Key.ToLowerInvariant()] = (string)kv.Value;
                        }
                    }
                    catch (System.Exception e)
                    {
                        Jotunn.Logger.LogWarning($"Couldn't read {file}: {e.Message}");
                    }
                }
            }
            return clip != null && s_captions.TryGetValue(clip, out string text) ? text : null;
        }

        /// <summary>The clips the agent may pick for a line: everything that isn't a moment's.</summary>
        public static List<string> Choosable => Names.Where(n => !Moments.Any(m => n.StartsWith(m))).ToList();

        private static bool IsClip(string file)
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            return ext == ".ogg" || ext == ".wav";
        }

        /// <summary>Client: load every clip into memory (once, at startup).</summary>
        public static IEnumerator LoadAll()
        {
            if (!Directory.Exists(Folder))
            {
                yield break;
            }
            foreach (string file in Directory.GetFiles(Folder).Where(IsClip))
            {
                AudioType type = Path.GetExtension(file).ToLowerInvariant() == ".wav" ? AudioType.WAV : AudioType.OGGVORBIS;
                using (UnityWebRequest req = UnityWebRequestMultimedia.GetAudioClip("file:///" + file.Replace('\\', '/'), type))
                {
                    yield return req.SendWebRequest();
                    if (req.result != UnityWebRequest.Result.Success)
                    {
                        Jotunn.Logger.LogWarning($"Couldn't load sound {Path.GetFileName(file)}: {req.error}");
                        continue;
                    }
                    AudioClip clip = DownloadHandlerAudioClip.GetContent(req);
                    clip.name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                    s_clips[clip.name] = clip;
                }
            }
            Jotunn.Logger.LogInfo($"Loaded {s_clips.Count} companion sound clips");
        }

        /// <summary>
        /// Server: a clip for this moment, random among its variants, or null if there's none. With a subject (e.g. the
        /// enemy, "troll"), its own clips come first: moment__subject, moment__subject2...; otherwise only the general
        /// ones (no "__").
        /// </summary>
        public static string ForMoment(string moment, string subject = null)
        {
            if (!string.IsNullOrEmpty(subject))
            {
                string prefix = moment + "__" + subject.ToLowerInvariant();
                var own = Names.Where(n => n == prefix || (n.StartsWith(prefix) && n.Substring(prefix.Length).All(char.IsDigit))).ToList();
                if (own.Count > 0)
                {
                    return own[Random.Range(0, own.Count)];
                }
            }
            var matches = Names.Where(n => !n.Contains("__") && (n == moment || (n.StartsWith(moment) && n.Length > moment.Length
                                                             && (n[moment.Length] == '_' || char.IsDigit(n[moment.Length]))))).ToList();
            return matches.Count > 0 ? matches[Random.Range(0, matches.Count)] : null;
        }

        /// <summary>A creature's clip subject: its name key, e.g. "$enemy_troll" -> "troll".</summary>
        public static string Subject(Character c) => c ? c.m_name.Replace("$enemy_", "").ToLowerInvariant() : null;

        /// <summary>Server: may a clip play now? (Keeps clips at least a couple of seconds apart.)</summary>
        public static bool TakeTurn()
        {
            if (Time.time - s_lastPlayed < MinGap)
            {
                return false;
            }
            s_lastPlayed = Time.time;
            return true;
        }

        /// <summary>Client: play a clip from this character (or, if it's about to go, where it stands).</summary>
        public static void Play(string name, Transform source, bool follow)
        {
            if (!s_clips.TryGetValue((name ?? "").ToLowerInvariant(), out AudioClip clip) || !source)
            {
                return;
            }
            var go = new GameObject("CompanionVoice");
            if (follow)
            {
                go.transform.SetParent(source, false);
                go.transform.localPosition = Vector3.up * 1.8f;
            }
            else
            {
                go.transform.position = source.position + Vector3.up * 1.8f;
            }
            AudioSource audio = go.AddComponent<AudioSource>();
            audio.clip = clip;
            audio.spatialBlend = 1f;
            audio.rolloffMode = AudioRolloffMode.Linear;
            audio.minDistance = 4f;
            audio.maxDistance = 45f;
            audio.volume = Mathf.Clamp01(Plugin.SoundVolume.Value);
            audio.outputAudioMixerGroup = SfxGroup();
            audio.Play();
            Object.Destroy(go, clip.length + 0.5f);
        }

        /// <summary>The game's sound-effects mixer group, borrowed from one of its own sound effects.</summary>
        private static AudioMixerGroup SfxGroup()
        {
            if (!s_sfxLooked && ZNetScene.instance)
            {
                s_sfxLooked = true;
                foreach (GameObject prefab in ZNetScene.instance.m_prefabs)
                {
                    if (prefab && prefab.name.StartsWith("sfx_") && prefab.GetComponentInChildren<AudioSource>(true) is AudioSource a && a.outputAudioMixerGroup)
                    {
                        s_sfxGroup = a.outputAudioMixerGroup;
                        break;
                    }
                }
            }
            return s_sfxGroup;
        }
    }
}
