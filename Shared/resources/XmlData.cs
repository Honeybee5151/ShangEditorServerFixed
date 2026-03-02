using Newtonsoft.Json;
using NLog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using Shared;
using Shared.terrain;

namespace Shared.resources
{
    //editor8182381 — Custom ground entry for JM maps with pixel-based tiles
    public class CustomGroundEntry
    {
        public ushort TypeCode;
        public string GroundId;
        public string GroundPixels;
        public byte[] DecodedPixels; // cached decoded RGB bytes (192), set once at load
        public bool NoWalk; // true if ground tile blocks movement
        public int BlendPriority = -1; // tile blend priority (-1 = default/lowest, higher wins at edges)
        public float Speed = 1.0f; // movement speed multiplier (1.0 = normal)
    }

    //editor8182381 — Custom object entry for JM maps with pixel-based sprites
    public class CustomObjectEntry
    {
        public ushort TypeCode;      // 0x9000+ assigned in Json2Wmap
        public string ObjectId;      // "custom_xxx" from JM
        public string ObjectPixels;  // base64 RGB pixels
        public string ObjectClass;   // "Object", "Wall", "Destructible", "Decoration", "Blocker"
        public byte SpriteSize;      // 8, 16, or 32 — sprite dimension in pixels
        public byte[] DecodedPixels; // cached decoded RGB bytes (size*size*3), set once at load
    }

    public class XmlData
    {
        public Dictionary<ushort, PlayerDesc> Classes = new Dictionary<ushort, PlayerDesc>();
        public Dictionary<string, ushort> DisplayIdToObjectType = new Dictionary<string, ushort>();
        public Dictionary<string, ushort> IdToObjectType = new Dictionary<string, ushort>(StringComparer.InvariantCultureIgnoreCase);
        public Dictionary<string, ushort> IdToTileType = new Dictionary<string, ushort>();
        public Dictionary<ushort, Item> Items = new Dictionary<ushort, Item>();
        public Dictionary<ushort, ObjectDesc> ObjectDescs = new Dictionary<ushort, ObjectDesc>();
        public Dictionary<ushort, string> ObjectTypeToId = new Dictionary<ushort, string>();
        public Dictionary<ushort, PortalDesc> Portals = new Dictionary<ushort, PortalDesc>();
        public Dictionary<ushort, SkinDesc> Skins = new Dictionary<ushort, SkinDesc>();
        public Dictionary<int, ItemType> SlotTypeToItemType = new Dictionary<int, ItemType>();
        public Dictionary<ushort, TileDesc> Tiles = new Dictionary<ushort, TileDesc>();
        public Dictionary<ushort, string> TileTypeToId = new Dictionary<ushort, string>();

        //editor8182381 — Custom ground/object storage per JM path
        public Dictionary<string, List<CustomGroundEntry>> JmCustomGrounds = new Dictionary<string, List<CustomGroundEntry>>();
        public Dictionary<string, List<CustomObjectEntry>> JmCustomObjects = new Dictionary<string, List<CustomObjectEntry>>();

        //editor8182381 — Global type code allocator for custom objects (thread-safe, prevents collisions across dungeons)
        private ushort _nextCustomObjTypeCode = 0x9000;
        private readonly object _customObjLock = new object();

        //editor8182381 — Allocate a unique type code for a custom object. Thread-safe.
        public ushort AllocateCustomObjTypeCode()
        {
            lock (_customObjLock) return _nextCustomObjTypeCode++;
        }

        //editor8182381 — Register custom object entries into shared dictionaries. Thread-safe.
        public void RegisterCustomObjects(List<CustomObjectEntry> entries)
        {
            lock (_customObjLock)
            {
                foreach (var co in entries)
                {
                    ObjectDescs[co.TypeCode] = new ObjectDesc(co.TypeCode, BuildCustomObjectXml(co));
                    IdToObjectType[co.ObjectId] = co.TypeCode;
                    ObjectTypeToId[co.TypeCode] = co.ObjectId;
                }
            }
        }

        //editor8182381 — Unregister custom object entries from shared dictionaries. Thread-safe.
        public void UnregisterCustomObjects(List<CustomObjectEntry> entries)
        {
            lock (_customObjLock)
            {
                foreach (var co in entries)
                {
                    ObjectDescs.Remove(co.TypeCode);
                    IdToObjectType.Remove(co.ObjectId);
                    ObjectTypeToId.Remove(co.TypeCode);
                }
            }
        }

        //editor8182381 — Build ObjectDesc XML for custom objects based on class
        private static XElement BuildCustomObjectXml(CustomObjectEntry co)
        {
            if (co.ObjectClass == "Blocker")
            {
                return new XElement("Object",
                    new System.Xml.Linq.XAttribute("type", $"0x{co.TypeCode:x4}"),
                    new System.Xml.Linq.XAttribute("id", co.ObjectId),
                    new XElement("Class", "GameObject"),
                    new XElement("Static"),
                    new XElement("OccupySquare"),
                    new XElement("EnemyOccupySquare")
                );
            }
            var is3D = co.ObjectClass == "Wall" || co.ObjectClass == "Destructible";
            var className = is3D ? "Wall" : "GameObject";
            var xml = new XElement("Object",
                new System.Xml.Linq.XAttribute("type", $"0x{co.TypeCode:x4}"),
                new System.Xml.Linq.XAttribute("id", co.ObjectId),
                new XElement("Class", className),
                new XElement("Static")
            );
            switch (co.ObjectClass)
            {
                case "Wall":
                    xml.Add(new XElement("FullOccupy"));
                    xml.Add(new XElement("BlocksSight"));
                    xml.Add(new XElement("OccupySquare"));
                    xml.Add(new XElement("EnemyOccupySquare"));
                    break;
                case "Destructible":
                    xml.Add(new XElement("FullOccupy"));
                    xml.Add(new XElement("BlocksSight"));
                    xml.Add(new XElement("OccupySquare"));
                    xml.Add(new XElement("EnemyOccupySquare"));
                    xml.Add(new XElement("Enemy"));
                    xml.Add(new XElement("MaxHitPoints", 100));
                    break;
                case "Decoration":
                    break;
                default:
                    xml.Add(new XElement("OccupySquare"));
                    xml.Add(new XElement("EnemyOccupySquare"));
                    break;
            }
            return xml;
        }

        //editor8182381 — Pre-allocate placeholder TileDesc for custom ground type codes 0x8000-0xEFFF
        public void RegisterCustomGroundRange()
        {
            var placeholderXml = XElement.Parse(
                "<Ground type=\"0x8000\" id=\"CustomGround\">" +
                "<Texture><File>lofiEnvironment2</File><Index>0x0b</Index></Texture>" +
                "</Ground>");
            for (ushort t = 0x8000; t <= 0xEFFF; t++)
            {
                if (!Tiles.ContainsKey(t))
                    Tiles[t] = new TileDesc(t, placeholderXml);
            }
        }

        private readonly Dictionary<string, WorldResource> Worlds = new Dictionary<string, WorldResource>();
        private readonly Dictionary<string, byte[]> WorldDataCache = new Dictionary<string, byte[]>();
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public XElement ObjectCombinedXML;
        public XElement CombinedXMLPlayers;
        public XElement GroundCombinedXML;
        public XElement SkinsCombinedXML;

        public void Initialize(bool exportXmls)
        {
            if (exportXmls)
            {
                ObjectCombinedXML = new XElement("Objects");
                CombinedXMLPlayers = new XElement("Objects");
                GroundCombinedXML = new XElement("Grounds");
                SkinsCombinedXML = new XElement("Objects");
            }
        }

        private void AddGrounds(XElement root, bool exportXmls = false) => root.Elements("Ground").Select(e =>
        {
            if (exportXmls)
                GroundCombinedXML.Add(e);

            var id = e.GetAttribute<string>("id");
            var type = e.GetAttribute<ushort>("type");

            if (TileTypeToId.ContainsKey(type))
                Log.Warn("'{0}' and '{1}' have the same type of '0x{2:x4}'", id, TileTypeToId[type], type);

            if (IdToTileType.ContainsKey(id))
                Log.Warn("'0x{0:x4}' and '0x{1:x4}' have the same id of '{2}'", type, IdToTileType[id], id);

            TileTypeToId[type] = id;
            IdToTileType[id] = type;

            Tiles[type] = new TileDesc(type, e);

            return e;
        }).ToArray();

        private void AddObjects(XElement root, bool exportXmls = false)
        {
            foreach (var e in root.Elements("Object"))
            {
                if (exportXmls)
                {
                    if (e.Element("Player") != null)
                        CombinedXMLPlayers.Add(e);
                    else if (e.Element("Skin") != null)
                        SkinsCombinedXML.Add(e);
                    else
                        ObjectCombinedXML.Add(e);
                }

                var cls = e.GetValue<string>("Class");
                if (string.IsNullOrWhiteSpace(cls))
                    continue;

                ushort type = 0;
                try
                {
                    type = e.GetAttribute<ushort>("type");
                }
                catch
                {
                    Log.Error("XML Error: " + e);
                }

                var id = e.GetAttribute<string>("id");
                var displayId = e.GetValue<string>("DisplayId");
                var displayName = string.IsNullOrWhiteSpace(displayId) ? id : displayId;

                if (cls == "PetAbility" || cls == "PetBehavior") // dont add this
                    return;

                if (ObjectTypeToId.ContainsKey(type))
                    Log.Warn("'{0}' and '{1}' have the same type of '0x{2:x4}'", id, ObjectTypeToId[type], type);

                if (IdToObjectType.ContainsKey(id))
                {
                    // to prevent the situation where 'Something' and 'something' or 'SOMETHING' is flagging as same even if they have different capitalization
                    if (ObjectTypeToId[IdToObjectType[id]].Equals(id))
                        Log.Warn("'0x{0:x4}' and '0x{1:x4}' have the same id of '{2}'", type, IdToObjectType[id], id);
                }

                ObjectTypeToId[type] = id;
                IdToObjectType[id] = type;
                DisplayIdToObjectType[displayName] = type;

                switch (cls)
                {
                    case "Equipment":
                    case "Dye":
                        Items[type] = new Item(type, e);
                        break;
                    case "Player":
                        var pDesc = Classes[type] = new PlayerDesc(type, e);
                        ObjectDescs[type] = Classes[type];
                        SlotTypeToItemType[pDesc.SlotTypes[0]] = ItemType.Weapon;
                        SlotTypeToItemType[pDesc.SlotTypes[1]] = ItemType.Ability;
                        SlotTypeToItemType[pDesc.SlotTypes[2]] = ItemType.Armor;
                        SlotTypeToItemType[pDesc.SlotTypes[3]] = ItemType.Ring;
                        break;
                    case "GuildHallPortal":
                    case "Portal":
                        Portals[type] = new PortalDesc(type, e);
                        ObjectDescs[type] = Portals[type];
                        break;
                    case "Skin":
                        Skins[type] = new SkinDesc(type, e);
                        break;
                    default:
                        ObjectDescs[type] = new ObjectDesc(type, e);
                        break;
                }
            }
        }

        public void LoadMaps(string basePath)
        {
			var isDocker = Environment.GetEnvironmentVariable("IS_DOCKER") != null;
			
            var directories = Directory.GetDirectories(basePath, "*", SearchOption.AllDirectories).ToList();
            directories.Add(basePath);
            foreach (var directory in directories)
            {
                var directoryName = directory.Replace($@"{basePath}", "").Replace("\\", "/");

                var jms = Directory.GetFiles(directory, "*.jm");
                foreach (var jm in jms)
                {
                    var id = $"{(directoryName == "" ? "" : $"{directoryName}/")}{Path.GetFileName(jm)}";
					if(id[0] == '/')
						id = id.Substring(1, id.Length - 1);

                    if (id == "Realm of the Mad God.jm")
                        WorldDataCache.Add(id, File.ReadAllBytes(jm));
                    else
                    {
                        var mapJson = Encoding.UTF8.GetString(File.ReadAllBytes(jm));

                        try
                        {
                            //editor8182381 — Parse JM with custom ground/object pixel data
                            var data = Json2Wmap.Convert(this, mapJson, out var customGrounds, out var customObjects);
                            WorldDataCache.Add(id, data);
                            if (customGrounds.Count > 0)
                                JmCustomGrounds[id] = customGrounds;
                            if (customObjects.Count > 0)
                                JmCustomObjects[id] = customObjects;
                        }
                        catch (Exception e)
                        {
                            Log.Error($"Exception: {e}");
                            Log.Error($"JM Path Error: {jm}");
                        }
                    }
                }
            }
        }

        public void LoadXmls(string basePath, string ext, bool exportXmls = false)
        {
            var xmls = Directory.GetFiles(basePath, ext, SearchOption.AllDirectories);
            for (var i = 0; i < xmls.Length; i++)
            {
                var xml = File.ReadAllText(xmls[i]);

                try
                {
                    ProcessXml(XElement.Parse(xml), exportXmls);
                }
                catch (Exception e)
                {
                    Log.Error("Exception: " + e.Message + "\n" + e.StackTrace);
                    Log.Error("XML Path Error: " + xmls[i]);
                }
            }
        }

        public WorldResource GetWorld(string dungeonName)
        {
            if (Worlds.TryGetValue(dungeonName, out var ret))
                return ret;
            return null;
        }

        public byte[] GetWorldData(string name)
        {
            if (WorldDataCache.TryGetValue(name, out var ret))
                return ret;
            return null;
        }

        public void AddWorlds(XElement root)
        {
            foreach (var e in root.Elements("World"))
            {
                var world = new WorldResource(e);
                if (Worlds.ContainsKey(world.IdName))
                    throw new Exception($"Error Loading: Duplicate IdName: {world.IdName}");
                Worlds[world.IdName] = world;
            }
        }

        private void ProcessXml(XElement root, bool exportXmls = false)
        { 
            AddWorlds(root);
            AddObjects(root, exportXmls);
            AddGrounds(root, exportXmls);
        }
    }
}
