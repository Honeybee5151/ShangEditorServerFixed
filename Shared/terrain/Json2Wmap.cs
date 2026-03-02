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

            //editor8182381 — Custom ground dedup: groundKey → type code (allocated via XmlData to skip prod codes)
            var customGroundMap = new Dictionary<string, ushort>();
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
                    //editor8182381 — Parse advanced ground properties (damage, sink, animate, push, slide)
                    var minDmg = (o.damage != null && o.damage.Length >= 1) ? o.damage[0] : 0;
                    var maxDmg = (o.damage != null && o.damage.Length >= 2) ? o.damage[1] : 0;
                    var sink = o.sink == true;
                    var animType = 0;
                    var animDx = 0f;
                    var animDy = 0f;
                    if (o.animate != null)
                    {
                        animType = o.animate.type == "Wave" ? 1 : o.animate.type == "Flow" ? 2 : 0;
                        animDx = o.animate.dx;
                        animDy = o.animate.dy;
                    }
                    var push = o.push == true;
                    var slideAmt = o.slide ?? 0f;
                    //editor8182381 — Dedup key includes all advanced properties to avoid merging distinct tiles
                    var groundKey = o.ground
                        + (o.blocked == true ? "|blocked" : "")
                        + (bp != -1 ? $"|bp{bp}" : "")
                        + (spd != 1.0f ? $"|spd{spd}" : "")
                        + (minDmg > 0 || maxDmg > 0 ? $"|dmg{minDmg}-{maxDmg}" : "")
                        + (sink ? "|sink" : "")
                        + (animType != 0 ? $"|anim{animType}_{animDx}_{animDy}" : "")
                        + (push ? "|push" : "")
                        + (slideAmt != 0 ? $"|slide{slideAmt}" : "");
                    if (!customGroundMap.TryGetValue(groundKey, out tileId))
                    {
                        tileId = data.AllocateCustomGroundTypeCode();
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
                        //editor8182381 — CustomGroundEntry with all advanced properties
                        customGrounds.Add(new CustomGroundEntry
                        {
                            TypeCode = tileId,
                            GroundId = o.ground,
                            GroundPixels = o.groundPixels,
                            DecodedPixels = decodedGndPixels,
                            NoWalk = o.blocked == true,
                            BlendPriority = bp,
                            Speed = spd,
                            MinDamage = minDmg,
                            MaxDamage = maxDmg,
                            Sink = sink,
                            AnimateType = animType,
                            AnimateDx = animDx,
                            AnimateDy = animDy,
                            Push = push,
                            SlideAmount = slideAmt
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
            //editor8182381 — Extended condition includes advanced properties (damage, sink, animate, push, slide)
            foreach (var cg in customGrounds)
            {
                if (cg.NoWalk || cg.BlendPriority != -1 || cg.Speed != 1.0f ||
                    cg.MinDamage > 0 || cg.MaxDamage > 0 || cg.Sink || cg.Push || cg.SlideAmount != 0)
                {
                    var xml = $"<Ground type=\"0x{cg.TypeCode:X4}\" id=\"{cg.GroundId}\">" +
                        "<Texture><File>lofiEnvironment2</File><Index>0x0b</Index></Texture>" +
                        (cg.NoWalk ? "<NoWalk/>" : "") +
                        (cg.BlendPriority != -1 ? $"<BlendPriority>{cg.BlendPriority}</BlendPriority>" : "") +
                        (cg.Speed != 1.0f ? $"<Speed>{cg.Speed}</Speed>" : "") +
                        //editor8182381 — Advanced property XML elements (damage, sink, animate, push, slide)
                        (cg.MinDamage > 0 ? $"<MinDamage>{cg.MinDamage}</MinDamage>" : "") +
                        (cg.MaxDamage > 0 ? $"<MaxDamage>{cg.MaxDamage}</MaxDamage>" : "") +
                        (cg.Sink ? "<Sink/>" : "") +
                        (cg.AnimateType != 0 ? $"<Animate dx=\"{cg.AnimateDx}\" dy=\"{cg.AnimateDy}\">{(cg.AnimateType == 1 ? "Wave" : "Flow")}</Animate>" : "") +
                        (cg.Push ? "<Push/>" : "") +
                        (cg.SlideAmount != 0 ? $"<SlideAmount>{cg.SlideAmount}</SlideAmount>" : "") +
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

        //editor8182381 — Animate sub-object for ground tile flow/wave animation
        private class json_animate
        {
            public string type;
            public float dx;
            public float dy;
        }

        //editor8182381 — Extended loc struct with custom ground/object fields
        private struct loc
        {
            public string ground;
            public string groundPixels;
            public bool? blocked;
            public int? blendPriority;
            public float? speed;
            //editor8182381 — Advanced ground tile fields from JM (damage, sink, animate, push, slide)
            public int[] damage;
            public bool? sink;
            public json_animate animate;
            public bool? push;
            public float? slide;
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
