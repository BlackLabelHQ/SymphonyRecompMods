using System;
using ImGuiNET;
using RecompOne.Runtime;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Modding;
using RecompOne.Runtime.Memory;
using Recompiled;
using Sotn;

public sealed class RapierPickupMod : IMod
{
    private const string Prefix = "mods.rapierPickup.";
    private const string CollectedKey = Prefix + "collected";
    private const int EquipItemDropEntityId = 3;
    private const HandItem PickupItem = HandItem.Rapier;
    private const string ItemName = "Rapier";
    private static readonly PickupLocation[] Locations =
    [
        new(7, 0, 150, 163),
        new(65, 72, 150, 163),
    ];

    private readonly record struct PickupLocation(int Stage, int Room, int X, int Y)
    {
        public string Key => $"{Stage}.{Room}";
    }

    private int spawnedStage = -1;
    private int spawnedRoom = -1;
    private int spawnedSlot = -1;
    private int itemCountAtSpawn;
    private int lastStage = -1;
    private int lastRoom = -1;
    private int retryCooldown;
    private string status = "Ready.";

    public void OnLoad()
    {
        Event.AddListener<VSyncEvent>(OnVSync);
        Event.AddListener<PlayerLoadedEvent>(OnPlayerLoaded);
        Event.AddListener<SaveLoadedEvent>(OnSaveLoaded);
        Event.AddListener<RoomLayerLoadEvent>(OnRoomLayerLoaded);
    }

    public void OnUnload()
    {
        Event.RemoveListener<VSyncEvent>(OnVSync);
        Event.RemoveListener<PlayerLoadedEvent>(OnPlayerLoaded);
        Event.RemoveListener<SaveLoadedEvent>(OnSaveLoaded);
        Event.RemoveListener<RoomLayerLoadEvent>(OnRoomLayerLoaded);
    }

    public void DrawSettings()
    {
        if (Game.Available && Game.InAlucardMode())
            ImGui.TextUnformatted($"Current: stage {(int)Game.StageId}, room {Game.Room}, x {Player.PosX}, y {Player.PosY}");
        else
            ImGui.TextUnformatted("Load an Alucard save to inspect current location.");

        ImGui.TextWrapped(status);
    }

    private void OnVSync(VSyncEvent e)
    {
        if (!Game.Available || !Game.InAlucardMode() || Game.IsLoading)
            return;

        var location = CurrentLocation();
        if (location == null)
        {
            status = $"Waiting for a pickup room. Current: stage {(int)Game.StageId}, room {Game.Room}.";
            return;
        }

        TrackRoomChange();

        if (Runtime.View.GetBool(CollectedKeyFor(location.Value)))
        {
            if (Inventory.OwnsHandItem((int)PickupItem))
                return;

            Runtime.View.SetBool(CollectedKeyFor(location.Value), false);
            Runtime.SaveView();
            status = $"{ItemName} was marked collected here, but this save does not have it; retrying spawn.";
            return;
        }

        if (spawnedSlot >= 0)
        {
            WatchVisiblePickup();
            return;
        }

        if (retryCooldown > 0)
        {
            retryCooldown--;
            return;
        }

        if (!TrySpawnVisibleItem(out string message) && message != "Ready.")
            status = message;
    }

    private void OnPlayerLoaded(PlayerLoadedEvent e) => TrySpawnForCurrentRoom("Player loaded.");

    private void OnSaveLoaded(SaveLoadedEvent e) => TrySpawnForCurrentRoom("Save loaded.");

    private void OnRoomLayerLoaded(RoomLayerLoadEvent e) => TrySpawnForCurrentRoom("Room loaded.");

    private void TrySpawnForCurrentRoom(string source)
    {
        var location = CurrentLocation();
        if (location == null || Runtime.View.GetBool(CollectedKeyFor(location.Value)))
            return;

        TrackRoomChange();

        if (spawnedSlot >= 0)
            return;

        if (TrySpawnVisibleItem(out string message))
            status = $"{source} Spawned {ItemName}.";
        else if (message != "Configured location is not in the current stage/room.")
            status = message;
    }

    private void TrackRoomChange()
    {
        int currentStage = Game.Available ? (int)Game.StageId : -1;
        int currentRoom = Game.Available ? Game.Room : -1;
        if (currentStage == lastStage && currentRoom == lastRoom)
            return;

        lastStage = currentStage;
        lastRoom = currentRoom;
        spawnedSlot = -1;
        retryCooldown = 0;
    }

    private static PickupLocation? CurrentLocation()
    {
        if (!Game.Available)
            return null;

        int stage = (int)Game.StageId;
        int room = Game.Room;
        foreach (var location in Locations)
        {
            if (location.Stage == stage && location.Room == room)
                return location;
        }

        return null;
    }

    private static string CollectedKeyFor(PickupLocation location) => $"{CollectedKey}.{location.Key}";

    private bool TrySpawnVisibleItem(out string message)
    {
        message = "Ready.";

        if (!Game.Available || !Game.InAlucardMode() || Runtime.Mem == null)
        {
            message = "Load an Alucard save before spawning.";
            return false;
        }

        var location = CurrentLocation();
        if (location == null)
        {
            message = "Configured location is not in the current stage/room.";
            return false;
        }

        if (spawnedStage == location.Value.Stage && spawnedRoom == location.Value.Room && spawnedSlot >= 0)
        {
            var existing = Entities.At(spawnedSlot);
            if (existing.IsAlive && existing.EntityId == EquipItemDropEntityId)
            {
                message = $"{ItemName} is already spawned.";
                return true;
            }

            spawnedSlot = -1;
        }

        var entity = Entities.GetFreeStage();
        if (!entity.IsValid)
        {
            message = "No free stage entity slot is available.";
            return false;
        }

        IMemory memory = Runtime.Mem!;
        uint update = FindMappedEquipItemDropUpdate();
        if (update == 0)
        {
            message = "EntityEquipItemDrop is not mapped in the active overlay.";
            return false;
        }

        entity.Write(new byte[Entity.Stride]);
        entity.PosX = location.Value.X;
        entity.PosY = location.Value.Y;
        entity.Update = update;
        entity.Params = (ushort)PickupItem;
        entity.Step = 0;
        entity.StepSub = 0;

        memory.WriteU16(entity.Addr + 0x26u, EquipItemDropEntityId);
        entity.FlagBits = EntityFlags.KeepAliveOffCamera | EntityFlags.NotAnEnemy;

        spawnedStage = location.Value.Stage;
        spawnedRoom = location.Value.Room;
        spawnedSlot = Entities.SlotOf(entity);
        itemCountAtSpawn = Inventory.GetHandCount(PickupItem);
        status = $"Spawned {ItemName} at stage {location.Value.Stage}, room {location.Value.Room}, x {location.Value.X}, y {location.Value.Y}.";
        return true;
    }

    private static uint FindMappedEquipItemDropUpdate()
    {
        foreach (string name in Dispatcher.ActiveNames)
        {
            if (!Dispatcher.Overlays.TryGetValue(name, out var overlay))
                continue;

            foreach (var (address, function) in overlay.Functions)
            {
                if (function.Method.Name.Contains("EntityEquipItemDrop", StringComparison.Ordinal))
                    return address;
            }
        }

        return 0;
    }

    private void WatchVisiblePickup()
    {
        if (spawnedSlot < 0)
            return;

        if (Inventory.GetHandCount(PickupItem) > itemCountAtSpawn)
        {
            MarkCollected();
            status = $"{ItemName} collected.";
            return;
        }

        var spawned = Entities.At(spawnedSlot);
        if (!spawned.IsAlive || spawned.EntityId != EquipItemDropEntityId)
        {
            spawnedSlot = -1;
            retryCooldown = 30;
            status = $"{ItemName} disappeared before inventory changed; retrying.";
        }
    }

    private void MarkCollected()
    {
        if (CurrentLocation() is { } location)
            Runtime.View.SetBool(CollectedKeyFor(location), true);
        Runtime.SaveView();
        spawnedSlot = -1;
    }

}
