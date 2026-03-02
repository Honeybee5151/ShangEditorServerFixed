//editor8182381 — Sends custom object pixel data to client for dungeon editor maps
using System;
using System.Collections.Generic;
using System.IO;
using Ionic.Zlib;
using NLog;
using Shared;
using Shared.resources;

namespace WorldServer.networking.packets.outgoing
{
    public class CustomObjectsMessage : OutgoingMessage
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        /// <summary>Pre-compressed blob (built once at world load). Preferred over Entries.</summary>
        public byte[] PreCompressed { get; set; }

        /// <summary>Fallback: raw entries to compress on-the-fly (only if PreCompressed is null).</summary>
        public List<CustomObjectEntry> Entries { get; set; }

        public override MessageId MessageId => MessageId.CUSTOM_OBJECTS;

        public override void Write(NetworkWriter wtr)
        {
            byte[] compressed;

            if (PreCompressed != null)
            {
                compressed = PreCompressed;
            }
            else
            {
                var entries = Entries ?? new List<CustomObjectEntry>();
                using (var ms = new MemoryStream())
                using (var bw = new NetworkWriter(ms))
                {
                    bw.Write(entries.Count);
                    foreach (var entry in entries)
                    {
                        bw.Write(entry.TypeCode);
                        bw.Write(entry.SpriteSize);
                        if (entry.SpriteSize > 0 && entry.DecodedPixels != null)
                        {
                            int expectedBytes = entry.SpriteSize * entry.SpriteSize * 3;
                            bw.Write(entry.DecodedPixels, 0, Math.Min(entry.DecodedPixels.Length, expectedBytes));
                            if (entry.DecodedPixels.Length < expectedBytes)
                                bw.Write(new byte[expectedBytes - entry.DecodedPixels.Length]);
                        }
                        // 0=Object(2D solid), 1=Destructible(3D breakable), 2=Decoration(2D walkable), 3=Wall(3D solid), 4=Blocker(invisible)
                        byte classFlag = 0;
                        if (entry.ObjectClass == "Destructible") classFlag = 1;
                        else if (entry.ObjectClass == "Decoration") classFlag = 2;
                        else if (entry.ObjectClass == "Wall") classFlag = 3;
                        else if (entry.ObjectClass == "Blocker") classFlag = 4;
                        bw.Write(classFlag);
                    }
                    bw.Flush();
                    compressed = ZlibStream.CompressBuffer(ms.ToArray());
                }
            }

            wtr.Write(compressed.Length);
            wtr.Write(compressed);
        }
    }
}
