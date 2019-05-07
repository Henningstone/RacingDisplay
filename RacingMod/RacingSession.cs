using System;
using VRage.Game.Components;
using VRage.ModAPI;
using Sandbox.ModAPI;
using VRage.Utils;
using VRage.Game;
using VRage.Game.ModAPI;
using System.Collections.Generic;
using System.Text;
using VRageMath;
using System.Linq;
using Draygo.API;
using Sandbox.Game;

namespace RacingMod
{
    [MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
    public class RacingSession : MySessionComponentBase
    {
        const ushort packetId = 1337;


        public static RacingSession Instance;
        public SortedDictionary<float, IMyBeacon> Nodes = new SortedDictionary<float, IMyBeacon>();
        public StringBuilder Text = new StringBuilder("Initializing...");

        private Dictionary<long, IMyCubeGrid> grids = new Dictionary<long, IMyCubeGrid>();
        private HudAPIv2 textApi;
        private HudAPIv2.HUDMessage hudMsg;
        const int numberWidth = 2; 
        const int nameWidth = 15;
        const float moveValue = 0.02f; // 1 m/s in 1 tick = 1 * 1/60
        private const int rankUpTime = 90;
        private Vector2D position = new Vector2D(-0.95, 0.90);
        private bool running = false;
        Dictionary<long, RacerInfo> previousValues = new Dictionary<long, RacerInfo>();
        readonly string hudHeader;
        int frameCount = 0;
        const string colorWhite = "<color=white>";
        const string colorStationary = "<color=255,124,124>";
        const string colorRankUp = "<color=124,255,154>";

        Color gateWaypointColor = new Color(0, 255, 255);
        const string gateWaypointName = "Checkpoint";
        const string gateWaypointDescription = "The next checkpoint in the race.";

        bool debug = false;
        bool crash = false;

        public RacingSession ()
        {
            hudHeader = "#".PadRight(numberWidth + 1) + "Name".PadRight(nameWidth + 1) + "Distance\n";
        }

        public override void BeforeStart ()
        {

            textApi = new HudAPIv2(CreateHudItems);


            MyAPIGateway.Utilities.MessageEntered += MessageEntered;
            if(MyAPIGateway.Multiplayer.IsServer)
            {
                MyVisualScriptLogicProvider.RemoveGPSForAll(gateWaypointName);
                MyAPIGateway.Entities.OnEntityAdd += OnEntityAdd;
                MyAPIGateway.Entities.OnEntityRemove += OnEntityRemove;

                HashSet<IMyEntity> temp = new HashSet<IMyEntity>();
                MyAPIGateway.Entities.GetEntities(temp);
                foreach (IMyEntity e in temp)
                    OnEntityAdd(e);
            }
            else
            {
                IMyGps temp = MyAPIGateway.Session.GPS.Create(gateWaypointName, gateWaypointDescription, Vector3D.Zero, true);
                MyAPIGateway.Session.GPS.RemoveLocalGps(temp);
                MyAPIGateway.Multiplayer.RegisterMessageHandler(packetId, ReceiveMessage);
            }


            running = true;
            Instance = this;
        }

        private void ReceiveMessage (byte [] obj)
        {

            try
            {
                string data = Encoding.ASCII.GetString(obj);
                Text.Clear();
                Text.Append(data);
            }
            catch (Exception e)
            {
                MyLog.Default.WriteLineAndConsole($"{e.Message}\n{e.StackTrace}");

                if (MyAPIGateway.Session?.Player != null)
                    MyAPIGateway.Utilities.ShowNotification($"[ ERROR: {GetType().FullName}: {e.Message} | Send SpaceEngineers.Log to mod author ]", 10000, MyFontEnum.Red);
            }
        }

        private void CreateHudItems ()
        {
            hudMsg = new HudAPIv2.HUDMessage(Text, position, HideHud: false, Font: "monospace");
        }

        private void MessageEntered (string messageText, ref bool sendToOthers)
        {
            if (messageText == "/rcd" && hudMsg != null)
            {
                hudMsg.Visible = !hudMsg.Visible;
                sendToOthers = false;
            }
            if (messageText == "/debug")
            {
                debug = !debug;
                sendToOthers = false;
            }

            if (messageText == "/crash")
            {
                crash = !crash;
                sendToOthers = false;
            }
        }

        private void RemoveWaypoint (long identityId)
        {
            MyVisualScriptLogicProvider.RemoveGPS(gateWaypointName, identityId);
        }

        private bool IsInCockpit (IMyPlayer arg, out IMyCockpit cockpit)
        {
            IMyEntity e = arg.Controller?.ControlledEntity?.Entity;
            if (e == null)
            {
                cockpit = null;
                return false;
            }
            cockpit = e as IMyCockpit;
            return cockpit != null;
        }

        protected override void UnloadData ()
        {
            if (running)
            {
                MyAPIGateway.Utilities.MessageEntered -= MessageEntered;
                MyAPIGateway.Entities.OnEntityAdd -= OnEntityAdd;
                MyAPIGateway.Entities.OnEntityRemove -= OnEntityRemove;

                if(MyAPIGateway.Multiplayer.IsServer)
                {
                    MyAPIGateway.Multiplayer.UnregisterMessageHandler(packetId, ReceiveMessage);
                }
            }
            if (textApi != null)
                textApi.Unload();

            Instance = null;
        }

        public override void UpdateAfterSimulation ()
        {
            if (!running)
                return;

            if (MyAPIGateway.Multiplayer.IsServer)
            {
                ProcessValues();
                BroadcastData();
                frameCount++;
            }
        }

        private void BroadcastData ()
        {
            byte [] bytes = Encoding.ASCII.GetBytes(Text.ToString());

            List<IMyPlayer> players = new List<IMyPlayer>();
            MyAPIGateway.Players.GetPlayers(players);
            foreach (IMyPlayer p in players)
            {
                if (p.SteamUserId == MyAPIGateway.Multiplayer.ServerId)
                    continue;

                MyAPIGateway.Multiplayer.SendMessageTo(1337, bytes, p.SteamUserId);
            }
        }

        private void OnEntityRemove (IMyEntity ent)
        {
            IMyCubeGrid myGrid = ent as IMyCubeGrid;
            if (myGrid != null && myGrid.GridSizeEnum == MyCubeSize.Small)
                grids.Remove(myGrid.EntityId);
        }

        private void OnEntityAdd (IMyEntity ent)
        {
            IMyCubeGrid myGrid = ent as IMyCubeGrid;
            if (myGrid != null && myGrid.GridSizeEnum == MyCubeSize.Small)
                grids [myGrid.EntityId] = myGrid;
        }

        void ProcessValues ()
        {

            Vector3 [] nodes = Nodes.Values.Select(b => (Vector3)b.WorldMatrix.Translation).ToArray();
            float [] nodeDist = GenerateNodeDistances(nodes);

            if (Nodes.Count < 2)
            {
                Text.Clear();
                Text.Append("Waiting for beacon nodes...").AppendLine();
                return;
            }

            //Dictionary<string, ValueEntry> values = new Dictionary<string, ValueEntry>();
            SortedDictionary<float, RacerInfo> values = new SortedDictionary<float, RacerInfo>(new DescendingComparer<float>());
            foreach (IMyCubeGrid g in grids.Values)
            {
                if (g.Physics == null)
                    continue;

                string name = g.CustomName;
                if (name.Length == 0 || name [0] != '#')
                    continue;

                // Find the closest node
                int closest = GetClosestIndex(nodes, g.GetPosition());

                Vector3? beg = null;
                if (closest > 0)
                    beg = nodes[closest - 1];
                Vector3? end = null;
                if (closest < nodes.Length - 1)
                    end = nodes [closest + 1];

                Vector3? destination;
                Vector3 pos = g.GetPosition();
                float temp = GetValue(pos, beg, nodes[closest], end, nodeDist[closest], out destination);

                if (temp > 0)
                {
                    RacerInfo value = new RacerInfo(temp, pos, 0, name, g.EntityId);
                    value.Destination = destination;
                    IMyPlayer p = MyAPIGateway.Players.GetPlayerControllingEntity(g);
                    if(p != null)
                        value.Controller = p.IdentityId;
                    values [temp] = value;
                }
                else
                {
                    RacerInfo previous;
                    if (previousValues.TryGetValue(g.EntityId, out previous) && previous.Controller != 0)
                        RemoveWaypoint(previous.Controller);
                }

            }
              
            BuildText(values);
        }

        void DrawRemoveWaypoint(long identityId, Vector3D coords)
        {
            RemoveWaypoint(identityId);
            MyVisualScriptLogicProvider.AddGPSObjective(gateWaypointName, gateWaypointDescription, coords, gateWaypointColor, 0, identityId);
        }

        void RenderWaypoint(RacerInfo current, RacerInfo previous)
        {
            long playerIdentity = current.Controller;
            long previousId = previous.Controller;

            if(playerIdentity == 0) // No controller currently
            {
                if(previousId != 0) // Controller left
                    RemoveWaypoint(previousId);
            }
            else // Has controller
            {
                if(previousId != 0 && playerIdentity != previousId) // Controller switched
                    RemoveWaypoint(previousId);

                if(!current.Destination.HasValue) // Before start or after finish
                {
                    if (previous.Destination.HasValue) // Player just left the track
                        RemoveWaypoint(playerIdentity);
                }
                else // Player is on the track
                {
                    // Player just took control, or Player just got on the track, or Player has crossed a node/checkpoint
                    if (previousId == 0 || playerIdentity != previousId || !previous.Destination.HasValue || current.Destination.Value != previous.Destination.Value) 
                        DrawRemoveWaypoint(playerIdentity, current.Destination.Value);
                }
            }
        }

        float [] GenerateNodeDistances (Vector3 [] nodes)
        {
            float [] dist = new float [nodes.Length];
            if (nodes.Length == 0)
                return dist;
            float cumulative = 0;
            dist [0] = 0;
            Vector3 prev = nodes [0];
            for (int i = 1; i < dist.Length; i++)
            {
                Vector3 v = nodes [i];
                cumulative += Vector3.Distance(prev, v);
                dist [i] = cumulative;
                prev = nodes [i];
            }
            return dist;
        }

        int GetClosestIndex (Vector3[] nodes, Vector3 value)
        {
            int closest = 0;
            float closestDist2 = float.MaxValue;
            for (int i = 0; i < nodes.Length; i++)
            {
                float dist2 = Vector3.DistanceSquared(nodes [i], value);
                if (dist2 < closestDist2)
                {
                    closestDist2 = dist2;
                    closest = i;
                }
            }
            return closest;
        }

        float GetValue (Vector3 gridPos, Vector3? beg, Vector3 mid, Vector3? end, float midDistance, out Vector3? destination)
        {
            if (!beg.HasValue && !end.HasValue)
            {
                destination = null;
                return -1;
            }

            Vector3 dir = gridPos - mid;

            float? before = null;
            float beforeLength2 = 0;
            if (beg.HasValue)
            {
                Vector3 beforeSegment = mid - beg.Value;
                beforeLength2 = beforeSegment.LengthSquared();
                before = ScalerProjection(dir, Vector3.Normalize(beforeSegment));
            }

            float? after = null;
            float afterLength2 = 0;
            if (end.HasValue)
            {
                Vector3 afterSegment = end.Value - mid;
                afterLength2 = afterSegment.LengthSquared();
                after = ScalerProjection(dir, Vector3.Normalize(afterSegment));
            }

            if (!after.HasValue)
            {
                if(before.Value < 0)
                {
                    destination = mid;
                    return midDistance + before.Value;
                }
                destination = null;
                return -1;
            }

            if (!before.HasValue)
            {
                if (after.Value > 0)
                {
                    destination = end;
                    return midDistance + after.Value;
                }
                destination = null;
                return -1;
            }

            if (after > 0)
            {
                destination = end;
                return midDistance + after.Value;
            }

            destination = mid;
            return midDistance + before.Value;
        }

        void BuildText (SortedDictionary<float, RacerInfo> values)
        {
            Text.Clear();
            if (values.Count == 0)
            {
                Text.Append("No racers in range.");
                previousValues.Clear();
            }
            else
            {
                Text.Append(hudHeader);
                bool drawWhite = false;

                Dictionary<long, RacerInfo> newPreviousValues = new Dictionary<long, RacerInfo>(values.Count);
                int i = 1;
                foreach(RacerInfo value in values.Values)
                {
                    RacerInfo current = value;
                    current.Rank = i;

                    string drawnColor = null;
                    RacerInfo previous;
                    if (previousValues.TryGetValue(current.GridId, out previous))
                    {
                        RenderWaypoint(current, previous);

                        if (!Moved(current, previous))
                        {
                            drawnColor = colorStationary;
                            current.RankUpFrame = previous.RankUpFrame;
                        }
                        else if (previousValues.Count == values.Count && current.Rank < previous.Rank)
                        {
                            current.RankUpFrame = frameCount;
                            drawnColor = colorRankUp;
                        }
                        else if (previous.RankUpFrame > 0)
                        {
                            if (previous.RankUpFrame + rankUpTime > frameCount)
                                current.RankUpFrame = previous.RankUpFrame;
                            drawnColor = colorRankUp;
                        }
                    }
                    else
                    {
                        if (current.Controller != 0 && current.Destination.HasValue)
                            DrawRemoveWaypoint(current.Controller, current.Destination.Value);
                    }

                    if (drawnColor != null)
                    {
                        Text.Append(drawnColor);
                        drawWhite = true;
                    }
                    else if (drawWhite)
                    {
                        Text.Append(colorWhite);
                        drawWhite = false;
                    }

                    // <num> 
                    Text.Append(SetLength(i, numberWidth)).Append(' ');

                    // <num> <name>
                    Text.Append(SetLength(current.Name, nameWidth, 1)).Append(' ');

                    // <num> <name> <distance>
                    Text.Append((int)current.Value).AppendLine();

                    newPreviousValues [current.GridId] = current;

                    i++;
                }
                previousValues = newPreviousValues;
            }
        }

        bool Moved(RacerInfo value, RacerInfo previous)
        {
            float x = Math.Abs(previous.Position.X - value.Position.X);
            if (x > moveValue)
                return true;

            float y = Math.Abs(previous.Position.Y - value.Position.Y);
            if (y > moveValue)
                return true;

            float z = Math.Abs(previous.Position.Z - value.Position.Z);
            if (z > moveValue)
                return true;
            return false;
        }

        string SetLength(object o, int length, int startIndex = 0)
        {
            string s = "";
            if(o != null)
                s = o.ToString();
            return s.PadRight(length + startIndex).Substring(startIndex, length);
        }

        /// <summary>
        /// Projects a value onto another vector.
        /// </summary>
        /// <param name="guide">Must be of length 1.</param>
        float ScalerProjection (Vector3 value, Vector3 guide)
        {
            float returnValue = Vector3.Dot(value, guide);
            if (float.IsNaN(returnValue))
                return 0;
            return returnValue;
        }

        struct RacerInfo
        {
            public long GridId;
            public float Value;
            public Vector3 Position;
            public int Rank;
            public string Name;
            public int RankUpFrame;
            public Vector3? Destination;
            public long Controller;

            public RacerInfo (float value, Vector3 position, int rank, string name, long gridId)
            {
                Value = value;
                Position = position;
                Rank = rank;
                Name = name;
                RankUpFrame = 0;
                GridId = gridId;
                Destination = null;
                Controller = 0;
            }
        }

        class DescendingComparer<T> : IComparer<T> where T : IComparable<T>
        {
            public int Compare (T x, T y)
            {
                return y.CompareTo(x);
            }
        }
    }
}