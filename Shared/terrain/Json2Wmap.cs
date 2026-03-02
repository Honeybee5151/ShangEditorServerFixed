//editor8182381 — Full custom ground/object pixel support for JM maps from the dungeon editor
using TKRShared;
using Ionic.Zlib;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using Shared.resources;

namespace Shared.terrain
{
    public class Json2Wmap
    {
        public static void Convert(XmlData data, string from, string to)
        {
            var x = Convert(data, File.ReadAllText(from), out _, out _);
            File.WriteAllBytes(to, x);
        }

        //editor8182381 — Convert JM JSON to wmap binary, extracting custom ground/object entries
        public static byte[] Convert(XmlData data, string json, out List<CustomGroundEntry> customGrounds, out List<CustomObjectEntry> customObjects)
        {
            var obj = JsonConvert.DeserializeObject<json_dat>(json);
            var dat = ZlibStream.UncompressBuffer(obj.data);
            var tileDict = new Dictionary<short, TerrainTile>();

            //editor8182381 — Custom ground dedup: groundKey → type code
            var customGroundMap = new Dictionary<string, ushort>();
            ushort nextCustomCode = 0x8000;
            customGrounds = new List<CustomGroundEntry>();

            //editor8182381 — Custom object dedup: (pixels|class) → typeCode/objectId
            var customObjPixelsMap = new Dictionary<string, string>();
            var customObjMap = new Dictionary<string, ushort>();
            customObjects = new List<CustomObjectEntry>();

            for (var i = 0; i < obj.dict.Length; i++)
            {
                var o = obj.dict[i];

                ushort tileId;
                if (o.ground == null)
                    tileId = 0xff;
                //editor8182381 — Handle custom ground tiles with embedded pixel data
                else if (o.ground.StartsWith("custom_"))
                {
                    var bp = o.blendPriority ?? -1;
                    var spd = o.speed ?? 1.0f;
                    var groundKey = o.ground + (o.blocked == true ? "|blocked" : "") + (bp != -1 ? $"|bp{bp}" : "") + (spd != 1.0f ? $"|spd{spd}" : "");
                    if (!customGroundMap.TryGetValue(groundKey, out tileId))
                    {
                        tileId = nextCustomCode++;
                        customGroundMap[groundKey] = tileId;
                        byte[] decodedGndPixels;
                        try { decodedGndPixels = System.Convert.FromBase64String(o.groundPixels ?? ""); }
                        catch { decodedGndPixels = new byte[192]; }
                        if (decodedGndPixels.Length < 192)
                        {
                            var padded = new byte[192];
                            Buffer.BlockCopy(decodedGndPixels, 0, padded, 0, decodedGndPixels.Length);
                            decodedGndPixels = padded;
                        }
                        customGrounds.Add(new CustomGroundEntry
                        {
                            TypeCode = tileId,
                            GroundId = o.ground,
                            GroundPixels = o.groundPixels,
                            DecodedPixels = decodedGndPixels,
                            NoWalk = o.blocked == true,
                            BlendPriority = bp,
                            Speed = spd
                        });
                    }
                }
                else
                    tileId = data.IdToTileType[o.ground];

                //editor8182381 — Handle custom object pixels
                string tileObjId = null;
                if (o.objs != null && o.objs.Length > 0 && !string.IsNullOrEmpty(o.objs[0].objectPixels))
                {
                    var objClass = o.objs[0].objectClass ?? "Object";
                    byte spriteSize = (byte)(o.objs[0].objectSize > 0 ? o.objs[0].objectSize : 8);
                    int expectedBytes = spriteSize * spriteSize * 3;
                    var dedupKey = o.objs[0].objectPixels + "|" + objClass;
                    if (!customObjMap.TryGetValue(dedupKey, out _))
                    {
                        var typeCode = data.AllocateCustomObjTypeCode();
                        var objId = $"cobj_{typeCode:x4}";
                        customObjMap[dedupKey] = typeCode;
                        customObjPixelsMap[dedupKey] = objId;
                        byte[] decodedPixels;
                        try { decodedPixels = System.Convert.FromBase64String(o.objs[0].objectPixels ?? ""); }
                        catch { decodedPixels = new byte[expectedBytes]; }
                        if (decodedPixels.Length < expectedBytes)
                        {
                            var padded = new byte[expectedBytes];
                            Buffer.BlockCopy(decodedPixels, 0, padded, 0, decodedPixels.Length);
                            decodedPixels = padded;
                        }
                        customObjects.Add(new CustomObjectEntry
                        {
                            TypeCode = typeCode,
                            ObjectId = objId,
                            ObjectPixels = o.objs[0].objectPixels,
                            ObjectClass = objClass,
                            SpriteSize = spriteSize,
                            DecodedPixels = decodedPixels
                        });
                    }
                    tileObjId = customObjPixelsMap[dedupKey];
                }
                //editor8182381 — Invisible blocker for multi-tile objects (no pixel data)
                else if (o.objs != null && o.objs.Length > 0 && o.objs[0].objectClass == "Blocker")
                {
                    var dedupKey = "blocker";
                    if (!customObjMap.TryGetValue(dedupKey, out _))
                    {
                        var typeCode = data.AllocateCustomObjTypeCode();
                        var objId = $"cobj_{typeCode:x4}";
                        customObjMap[dedupKey] = typeCode;
                        customObjPixelsMap[dedupKey] = objId;
                        customObjects.Add(new CustomObjectEntry
                        {
                            TypeCode = typeCode,
                            ObjectId = objId,
                            ObjectClass = "Blocker",
                            SpriteSize = 0,
                            DecodedPixels = null
                        });
                    }
                    tileObjId = customObjPixelsMap[dedupKey];
                }
                else
                {
                    tileObjId = o.objs?[0].id;
                }

                tileDict[(short)i] = new TerrainTile()
                {
                    TileId = tileId,
                    TileObj = tileObjId,
                    Name = o.objs == null ? "" : o.objs[0].name ?? "",
                    Terrain = TerrainType.None,
                    Region = o.regions == null ? TileRegion.None : (TileRegion)Enum.Parse(typeof(TileRegion), o.regions[0].id.Replace(' ', '_'))
                };
            }

            //editor8182381 — Override TileDesc for custom ground tiles with special properties
            foreach (var cg in customGrounds)
            {
                if (cg.NoWalk || cg.BlendPriority != -1 || cg.Speed != 1.0f)
                {
                    var xml = $"<Ground type=\"0x{cg.TypeCode:X4}\" id=\"{cg.GroundId}\">" +
                        "<Texture><File>lofiEnvironment2</File><Index>0x0b</Index></Texture>" +
                        (cg.NoWalk ? "<NoWalk/>" : "") +
                        (cg.BlendPriority != -1 ? $"<BlendPriority>{cg.BlendPriority}</BlendPriority>" : "") +
                        (cg.Speed != 1.0f ? $"<Speed>{cg.Speed}</Speed>" : "") +
                        "</Ground>";
                    data.Tiles[cg.TypeCode] = new TileDesc(cg.TypeCode, System.Xml.Linq.XElement.Parse(xml));
                }
            }

            var tiles = new TerrainTile[obj.width, obj.height];

            using (var rdr = new NetworkReader(new MemoryStream(dat)))
                for (var y = 0; y < obj.height; y++)
                    for (var x = 0; x < obj.width; x++)
                        tiles[x, y] = tileDict[rdr.ReadInt16()];

            return WorldMapExporter.Export(tiles);
        }

        private struct json_dat
        {
            public byte[] data;
            public loc[] dict;
            public int height;
            public int width;
        }

        //editor8182381 — Extended loc struct with custom ground/object fields
        private struct loc
        {
            public string ground;
            public string groundPixels;
            public bool? blocked;
            public int? blendPriority;
            public float? speed;
            public obj[] objs;
            public obj[] regions;
        }

        //editor8182381 — Extended obj struct with pixel/class/size fields
        private struct obj
        {
            public string id;
            public string name;
            public string objectPixels;
            public string objectClass;
            public int objectSize;
        }
    }
}
