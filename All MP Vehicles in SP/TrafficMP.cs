using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using GTA;
using GTA.Math;
using GTA.Native;

public class TrafficMP : Script
{
    private class GhostSlot
    {
        public Vehicle Vehicle;
        public Ped Driver;
        public Blip Blip;
    }

    private readonly List<GhostSlot> _slots = new List<GhostSlot>();

    private int _nextSpawnTime = 0;
    private int _nextSearchTime = 0;
    private int _resumeSpawnTime = 0;
    private bool _wasRestrictedState = false;

    private float SpawnDistance = 300.0f;
    private float DespawnDistance = 500.0f;
    private int RespawnDelayMs = 3000;
    private const int SearchIntervalMs = 500;

    private const int MissionGraceMs = 5000;

    private int _blipColor;
    private int _trafficBlipConfig;
    private int _streetFlag;
    private int _maxVehicles;
    private int _clearAreaFlag;

    public static int disableTaxiFlag;

    private int _spawnCounter = 0;
    private static readonly Random _rnd = new Random();

    private readonly HashSet<int> _dlcModelHashes = new HashSet<int>();
    private int _lastPlayerVehicleHandle = 0;

    private readonly HashSet<string> _richZones = new HashSet<string>
    {
        "RICHM",  // Richman
        "RGLEN",  // Richman Glen
        "ROCKF",  // Rockford Hills
        "VINE",   // Vinewood
        "DTVINE", // Downtown Vinewood
        "WVINE",  // West Vinewood
        "CHIL",   // Vinewood Hills
        "PBLUFF", // Pacific Bluffs
        "GOLF",   // GWC and Golfing Society
        "MORN",   // Morningwood
        "OBSERV"  // Galileo Observatory
    };

    private readonly HashSet<string> _blockedSpawnZones = new HashSet<string>
    {
        "AIRP",   // Los Santos International Airport
        "ARMYB",  // Fort Zancudo
        "JAIL"    // Bolingbroke Penitentiary
    };

    public TrafficMP()
    {
        Tick += OnTick;
        Aborted += OnAborted;

        try
        {
            ScriptSettings config = ScriptSettings.Load("Scripts\\AllMpVehiclesInSp.ini");
            _blipColor = config.GetValue<int>("MAIN", "blip_color_traffic", 3);
            _trafficBlipConfig = config.GetValue<int>("MAIN", "traffic_cars_blips", 0);
            _streetFlag = config.GetValue<int>("MAIN", "spawn_traffic", 1);
            RespawnDelayMs = config.GetValue<int>("MAIN", "time_traffic_gen", 3000);

            _maxVehicles = config.GetValue<int>("MAIN", "max_traffic_vehicles", 3);
            if (_maxVehicles < 1) _maxVehicles = 1;
            if (_maxVehicles > 10) _maxVehicles = 10;

            SpawnDistance = config.GetValue<float>("ADVANCED", "SpawnDistance", 300.0f);
            DespawnDistance = config.GetValue<float>("ADVANCED", "DespawnDistance", 500.0f);

            _clearAreaFlag = config.GetValue<int>("ADVANCED", "ClearSpawnArea", 0);

            BuildDlcCache();
        }
        catch (Exception ex)
        {
            _streetFlag = 0;
            Notifier.Show("~r~TrafficMP init error:~w~ " + ex.Message);
        }
    }

    private void BuildDlcCache()
    {
        try
        {
            FieldInfo[] fields = typeof(VehList).GetFields(BindingFlags.Public | BindingFlags.Static);
            foreach (var field in fields)
            {
                if (field.FieldType == typeof(List<string>))
                {
                    List<string> list = (List<string>)field.GetValue(null);
                    if (list != null)
                        foreach (string modelName in list) _dlcModelHashes.Add(Game.GenerateHash(modelName));
                }
                else if (field.FieldType == typeof(string))
                {
                    string modelName = (string)field.GetValue(null);
                    if (!string.IsNullOrEmpty(modelName)) _dlcModelHashes.Add(Game.GenerateHash(modelName));
                }
            }
        }
        catch (Exception ex)
        {
            Notifier.Show("~r~TrafficMP Cache Error:~w~ " + ex.Message);
        }
    }

    private bool IsRestrictedGameState()
    {
        if (Game.IsLoading) return true;
        if (Function.Call<bool>(Hash.GET_MISSION_FLAG)) return true;
        if (Function.Call<bool>(Hash.IS_CUTSCENE_PLAYING)) return true;
        if (Function.Call<bool>(Hash.IS_PLAYER_SWITCH_IN_PROGRESS)) return true;

        Ped p = Game.Player.Character;
        if (p == null || !p.Exists() || p.IsDead) return true;

        return false;
    }

    private void OnTick(object sender, EventArgs e)
    {
        if (_streetFlag == 0) return;

        if (IsRestrictedGameState())
        {
            if (!_wasRestrictedState)
            {
                ReleaseAll();
                _wasRestrictedState = true;
            }
            return;
        }

        if (_wasRestrictedState)
        {
            _wasRestrictedState = false;
            _resumeSpawnTime = Game.GameTime + MissionGraceMs;
        }

        if (Game.GameTime < _resumeSpawnTime) return;

        Ped player = Game.Player.Character;

        if (player.IsInVehicle())
        {
            Vehicle playerVeh = player.CurrentVehicle;
            if (playerVeh.Handle != _lastPlayerVehicleHandle)
            {
                if (IsDlcVehicle(playerVeh))
                    _nextSpawnTime = Game.GameTime + RespawnDelayMs;

                _lastPlayerVehicleHandle = playerVeh.Handle;
            }
        }
        else
        {
            _lastPlayerVehicleHandle = 0;
        }

        MaintainSlots(player);

        if (_slots.Count < _maxVehicles &&
            Game.GameTime >= _nextSpawnTime &&
            Game.GameTime >= _nextSearchTime)
        {
            AttemptSpawnOptimized();
            _nextSearchTime = Game.GameTime + SearchIntervalMs;
        }
    }

    private void MaintainSlots(Ped player)
    {
        for (int i = _slots.Count - 1; i >= 0; i--)
        {
            GhostSlot slot = _slots[i];

            if (slot.Vehicle == null || !slot.Vehicle.Exists() || slot.Vehicle.IsDead)
            {
                ReleaseSlot(slot);
                _slots.RemoveAt(i);
                _nextSpawnTime = Game.GameTime + RespawnDelayMs;
                continue;
            }

            if (player.IsInVehicle() && player.CurrentVehicle.Handle == slot.Vehicle.Handle)
            {
                ReleaseSlot(slot);
                _slots.RemoveAt(i);
                _nextSpawnTime = Game.GameTime + RespawnDelayMs;
                continue;
            }

            if (slot.Driver == null || !slot.Driver.Exists() || slot.Driver.IsDead)
            {
                ReleaseSlot(slot);
                _slots.RemoveAt(i);
                _nextSpawnTime = Game.GameTime + RespawnDelayMs;
                continue;
            }

            if (player.Position.DistanceTo(slot.Vehicle.Position) > DespawnDistance)
            {
                DeleteSlot(slot);
                _slots.RemoveAt(i);
                _nextSpawnTime = Game.GameTime + RespawnDelayMs;
            }
        }
    }

    private bool IsDlcVehicle(Vehicle v)
    {
        return _dlcModelHashes.Contains(v.Model.Hash);
    }

    private void ReleaseSlot(GhostSlot slot)
    {
        if (slot.Blip != null && slot.Blip.Exists()) slot.Blip.Delete();
        slot.Blip = null;

        if (slot.Driver != null && slot.Driver.Exists()) slot.Driver.MarkAsNoLongerNeeded();
        slot.Driver = null;

        if (slot.Vehicle != null && slot.Vehicle.Exists()) slot.Vehicle.MarkAsNoLongerNeeded();
        slot.Vehicle = null;
    }

    private void DeleteSlot(GhostSlot slot)
    {
        if (slot.Blip != null && slot.Blip.Exists()) slot.Blip.Delete();
        if (slot.Driver != null && slot.Driver.Exists()) slot.Driver.Delete();
        if (slot.Vehicle != null && slot.Vehicle.Exists()) slot.Vehicle.Delete();
        slot.Blip = null;
        slot.Driver = null;
        slot.Vehicle = null;
    }

    private void ReleaseAll()
    {
        foreach (GhostSlot slot in _slots) ReleaseSlot(slot);
        _slots.Clear();
    }

    private const int NodeFlagOffRoad = 1;
    private const int NodeFlagSwitchedOff = 8;
    private const int NodeFlagDeadEnd = 32;
    private const int NodeFlagHighway = 64;
    private const int NodeFlagJunction = 128;
    private const int NodeFlagWater = 1024;

    private const float LaneWidth = 5.4f;

    private void AttemptSpawnOptimized()
    {
        Ped playerPed = Game.Player.Character;
        Vector3 playerPos = playerPed.Position;

        Vector3 searchDir = playerPed.IsInVehicle()
            ? playerPed.CurrentVehicle.ForwardVector
            : playerPed.ForwardVector;
        searchDir.Z = 0f;
        if (searchDir.LengthSquared() < 0.001f) searchDir = Vector3.RelativeFront;
        searchDir.Normalize();

        Vector3 searchPos = playerPos + searchDir * SpawnDistance;

        Vector3 nodePos;
        float nodeHeading;
        bool isHighway;
        if (!TryFindRoadNode(searchPos, searchDir, playerPos, out nodePos, out nodeHeading, out isHighway))
            return;

        string nodeZone = Function.Call<string>(Hash.GET_NAME_OF_ZONE, nodePos.X, nodePos.Y, nodePos.Z);
        if (_blockedSpawnZones.Contains(nodeZone)) return;

        Vector3 spawnPos;
        float spawnHeading;
        if (!TryGetLaneCenter(nodePos, nodeHeading, out spawnPos, out spawnHeading))
        {
            spawnPos = nodePos;
            spawnHeading = nodeHeading;
        }

        if (_clearAreaFlag == 1)
        {
            Function.Call(Hash.CLEAR_AREA_OF_VEHICLES, spawnPos.X, spawnPos.Y, spawnPos.Z, 6.0f, false, false, false, false, false);
        }
        else
        {
            Vehicle[] occupying = World.GetNearbyVehicles(spawnPos, 7.0f);
            if (occupying != null && occupying.Length > 0) return;
        }

        float cruiseSpeed = isHighway
            ? 18.0f + (float)_rnd.NextDouble() * 5.0f
            : 10.0f + (float)_rnd.NextDouble() * 4.0f;

        float initialSpeed = cruiseSpeed;
        Vehicle[] around = World.GetNearbyVehicles(spawnPos, 25.0f);
        if (around != null && around.Length > 0) initialSpeed = 3.0f;

        try
        {
            string modelToSpawn = GetNextModelName(spawnPos);
            SpawnGhostCar(spawnPos, spawnHeading, modelToSpawn, cruiseSpeed, initialSpeed);
        }
        catch (Exception ex)
        {
            Notifier.Show("TrafficMP Spawn Error: " + ex.Message);
        }
    }

    private bool TryFindRoadNode(Vector3 searchPos, Vector3 searchDir, Vector3 playerPos,
        out Vector3 nodePos, out float nodeHeading, out bool isHighway)
    {
        nodePos = Vector3.Zero;
        nodeHeading = 0f;
        isHighway = false;

        Vector3 desired = searchPos + searchDir * 100.0f;

        OutputArgument outPos = new OutputArgument();
        OutputArgument outHead = new OutputArgument();

        for (int nth = 1; nth <= 6; nth++)
        {
            if (!Function.Call<bool>(Hash.GET_NTH_CLOSEST_VEHICLE_NODE_FAVOUR_DIRECTION,
                searchPos.X, searchPos.Y, searchPos.Z,
                desired.X, desired.Y, desired.Z,
                nth, outPos, outHead, 0, 3.0f, 0.0f))
                continue;

            Vector3 p = outPos.GetResult<Vector3>();
            if (p == Vector3.Zero) continue;

            float dist = playerPos.DistanceTo(p);
            if (dist <= 60.0f) continue;                    
            if (dist >= DespawnDistance - 50.0f) continue; 

            OutputArgument outDensity = new OutputArgument();
            OutputArgument outFlags = new OutputArgument();
            Function.Call<bool>(Hash.GET_VEHICLE_NODE_PROPERTIES, p.X, p.Y, p.Z, outDensity, outFlags);
            int flags = outFlags.GetResult<int>();

            if ((flags & (NodeFlagOffRoad | NodeFlagSwitchedOff | NodeFlagDeadEnd | NodeFlagJunction | NodeFlagWater)) != 0)
                continue;

            nodePos = p;
            nodeHeading = outHead.GetResult<float>();
            isHighway = (flags & NodeFlagHighway) != 0;
            return true;
        }

        return false;
    }

    private bool TryGetLaneCenter(Vector3 nodePos, float nodeHeading, out Vector3 lanePos, out float laneHeading)
    {
        lanePos = nodePos;
        laneHeading = nodeHeading;

        OutputArgument outNodeA = new OutputArgument();
        OutputArgument outNodeB = new OutputArgument();
        OutputArgument outLanesAB = new OutputArgument();
        OutputArgument outLanesBA = new OutputArgument();
        OutputArgument outMedian = new OutputArgument();

        if (!Function.Call<bool>(Hash.GET_CLOSEST_ROAD,
            nodePos.X, nodePos.Y, nodePos.Z, 1.0f, 1,
            outNodeA, outNodeB, outLanesAB, outLanesBA, outMedian, false))
            return false;

        Vector3 a = outNodeA.GetResult<Vector3>();
        Vector3 b = outNodeB.GetResult<Vector3>();
        int lanesAB = outLanesAB.GetResult<int>();
        int lanesBA = outLanesBA.GetResult<int>();
        float median = outMedian.GetResult<float>();

        Vector3 segFlat = new Vector3(b.X - a.X, b.Y - a.Y, 0f);
        if (segFlat.LengthSquared() < 1.0f) return false;
        if (lanesAB < 0 || lanesAB > 8 || lanesBA < 0 || lanesBA > 8 || lanesAB + lanesBA == 0) return false;
        if (median < 0f || median > 15.0f) median = 0f;

        float hRad = nodeHeading * (float)Math.PI / 180.0f;
        Vector3 travelDir = new Vector3(-(float)Math.Sin(hRad), (float)Math.Cos(hRad), 0f);
        Vector3 right = Vector3.Cross(travelDir, Vector3.WorldUp);

        bool alongAB = Vector3.Dot(segFlat.Normalized, travelDir) >= 0f;
        int laneCount = alongAB ? lanesAB : lanesBA;
        int oppositeLanes = alongAB ? lanesBA : lanesAB;
        if (laneCount <= 0) return false;

        int maxLaneIndex = Math.Min(laneCount, 3) - 1;
        int laneIndex = _rnd.Next(maxLaneIndex + 1);

        for (int i = laneIndex; i >= 0; i--)
        {
            float offset;
            if (oppositeLanes == 0)
            {
                offset = LaneWidth * (i + 0.5f) - (LaneWidth * laneCount) * 0.5f;
            }
            else
            {
                offset = median * 0.5f + LaneWidth * (i + 0.5f);
            }

            Vector3 candidate = nodePos + right * offset;
            if (Function.Call<bool>(Hash.IS_POINT_ON_ROAD, candidate.X, candidate.Y, candidate.Z, 0))
            {
                lanePos = candidate;
                return true;
            }
        }

        return false;
    }

    private string GetNextModelName(Vector3 spawnPos)
    {
        _spawnCounter++;

        if (_spawnCounter == 1)
        {
            if (VehList.models_latest.Count > 0)
                return VehList.models_latest[_rnd.Next(VehList.models_latest.Count)];
            return "vivanite2";
        }

        if (disableTaxiFlag != 1 && _spawnCounter % 9 == 2)
            return "vivanite2";

        return GetModelFromContext(spawnPos);
    }

    private string GetModelFromContext(Vector3 position)
    {
        string zoneName = Function.Call<string>(Hash.GET_NAME_OF_ZONE, position.X, position.Y, position.Z);
        bool isRichZone = _richZones.Contains(zoneName);

        int vclass = 0;

        if (isRichZone)
        {
            int[] richClasses = { 5, 6, 7 };
            vclass = richClasses[_rnd.Next(richClasses.Length)];
        }
        else
        {
            Vehicle[] nearbyVehs = World.GetNearbyVehicles(Game.Player.Character.Position, 100.0f);
            bool foundValid = false;

            if (nearbyVehs.Length > 0)
            {
                Vehicle randomNeighbor = nearbyVehs[_rnd.Next(nearbyVehs.Length)];
                if (randomNeighbor.Exists())
                {
                    int c = (int)randomNeighbor.ClassType;
                    if (c != 5 && c != 6 && c != 7 && c >= 0 && c <= 13)
                    {
                        vclass = c;
                        foundValid = true;
                    }
                }
            }

            if (!foundValid)
            {
                int[] poorClasses = { 0, 1, 2, 3, 4, 8, 9, 10, 11, 12, 13 };
                vclass = poorClasses[_rnd.Next(poorClasses.Length)];
            }
        }

        string model_name = "vivanite2";

        switch (vclass)
        {
            case 0: model_name = GetRandomModel(VehList.models_compacts); break;
            case 1: model_name = GetRandomModel(VehList.models_sedans); break;
            case 2: model_name = GetRandomModel(VehList.models_suvs); break;
            case 3: model_name = GetRandomModel(VehList.models_coupes); break;
            case 4: model_name = GetRandomModel(VehList.models_muscle); break;
            case 5: model_name = GetRandomModel(VehList.models_sportclassic); break;
            case 6: model_name = GetRandomModel(VehList.models_sportclassic); break;
            case 7: model_name = GetRandomModel(VehList.models_supers); break;
            case 8: model_name = GetRandomModel(VehList.models_motorcycles); break;
            case 9: model_name = GetRandomModel(VehList.models_offroad); break;
            case 10: model_name = GetRandomModel(VehList.models_industrial); break;
            case 11: model_name = GetRandomModel(VehList.models_industrial); break;
            case 12: model_name = GetRandomModel(VehList.models_vans); break;
            case 13: model_name = GetRandomModel(VehList.models_cycles); break;
        }

        return model_name;
    }

    private string GetRandomModel(List<string> list)
    {
        if (list == null || list.Count == 0) return "vivanite2";
        return list[_rnd.Next(list.Count)];
    }

    private void SpawnGhostCar(Vector3 position, float heading, string modelName, float cruiseSpeed, float initialSpeed)
    {
        Model carModel = new Model(modelName);

        if (!carModel.IsValid || !carModel.IsInCdImage)
        {
            if (modelName != "vivanite2") SpawnGhostCar(position, heading, "vivanite2", cruiseSpeed, initialSpeed);
            return;
        }

        if (!carModel.Request(500))
        {
            carModel.MarkAsNoLongerNeeded();
            return;
        }

        Vehicle vehicle = World.CreateVehicle(carModel, position, heading);
        carModel.MarkAsNoLongerNeeded();

        if (vehicle == null) return;

        Function.Call(Hash.SET_VEHICLE_ON_GROUND_PROPERLY, vehicle, 5.0f);

        GhostSlot slot = new GhostSlot { Vehicle = vehicle };

        vehicle.IsPersistent = true;
        vehicle.IsEngineRunning = true;
        vehicle.AreLightsOn = true;

        if (modelName.ToLower() == "vivanite2")
        {
            vehicle.Mods.CustomPrimaryColor = Color.White;
            vehicle.Mods.CustomSecondaryColor = Color.White;
            Function.Call(Hash.SET_VEHICLE_MOD_KIT, vehicle, 0);
            Function.Call(Hash.SET_VEHICLE_MOD, vehicle, 48, 0, false);
            vehicle.LockStatus = VehicleLockStatus.CannotEnter;

            slot.Driver = vehicle.CreateRandomPedOnSeat(VehicleSeat.Driver);
            if (slot.Driver != null)
            {
                slot.Driver.IsVisible = false;
                slot.Driver.CanBeTargetted = false;
                slot.Driver.BlockPermanentEvents = true;
                slot.Driver.IsInvincible = true;
                slot.Driver.CanRagdoll = false;
                slot.Driver.CanBeDraggedOutOfVehicle = false;
            }
        }
        else
        {
            slot.Driver = vehicle.CreateRandomPedOnSeat(VehicleSeat.Driver);
            if (slot.Driver != null)
            {
                slot.Driver.IsVisible = true;
                slot.Driver.CanBeTargetted = true;
                slot.Driver.BlockPermanentEvents = false;
            }
        }

        if (slot.Driver == null || !slot.Driver.Exists())
        {
            vehicle.Delete();
            return;
        }

        Function.Call(Hash.SET_DRIVER_ABILITY, slot.Driver, 1.0f);
        Function.Call(Hash.SET_DRIVER_AGGRESSIVENESS, slot.Driver, 0.0f);
        Function.Call(Hash.SET_VEHICLE_FORWARD_SPEED, vehicle, initialSpeed);

        slot.Driver.Task.CruiseWithVehicle(vehicle, cruiseSpeed, DrivingStyle.Normal);

        if (_trafficBlipConfig == 1)
            slot.Blip = CreateMarkerAboveCar(vehicle);

        _slots.Add(slot);
    }

    private Blip CreateMarkerAboveCar(Vehicle car)
    {
        Blip blip = Function.Call<Blip>(Hash.ADD_BLIP_FOR_ENTITY, car);
        Function.Call(Hash.SET_BLIP_SPRITE, blip, 1);
        Function.Call(Hash.SET_BLIP_COLOUR, blip, _blipColor);
        blip.Name = "Unique vehicle";
        return blip;
    }

    private void OnAborted(object sender, EventArgs e)
    {
        foreach (GhostSlot slot in _slots) DeleteSlot(slot);
        _slots.Clear();
    }
}
