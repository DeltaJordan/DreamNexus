﻿using SkyEditor.IO.Binary;
using SkyEditor.RomEditor.Domain.Common.Structures;
using SkyEditor.RomEditor.Domain.Rtdx.Constants;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SkyEditor.RomEditor.Domain.Rtdx.Structures
{
    public interface IDungeonBalance
    {
        DungeonBalance.Entry GetEntry(int index, bool temporary = false);
        Task<(byte[] bin, byte[] ent)> Build();
    }

    public class DungeonBalance : IDungeonBalance
    {
        public DungeonBalance(byte[] binData, byte[] entData)
        {
            IReadOnlyBinaryDataAccessor binFile = new BinaryFile(binData);
            IReadOnlyBinaryDataAccessor entFile = new BinaryFile(entData);

            var entCount = entFile.Length / sizeof(uint) - 1;
            EntryData = new IReadOnlyBinaryDataAccessor[entCount];
            Entries = new Entry[entCount];
            for (var i = 0; i < entCount; i++)
            {
                var curr = entFile.ReadInt32(i * sizeof(int));
                var next = entFile.ReadInt32((i + 1) * sizeof(int));
                EntryData[i] = binFile.Slice(curr, next - curr);
            }
        }

        public DungeonBalance()
        {
            EntryData = new IBinaryDataAccessor[0];
            Entries = new Entry[(int)DungeonIndex.END];
            for (int i = 0; i < (int)DungeonIndex.END; i++)
            {
                Entries[i] = new Entry(0);
            }
        }

        public async Task<(byte[] bin, byte[] ent)> Build()
        {
            MemoryStream bin = new MemoryStream();
            var entryPointers = new List<int>();

            // Compress entries in parallel
            var compressedEntries = await Task.WhenAll(Entries.Select((entry, index) => Task.Run(() =>
            {   
                if (entry == null)
                {
                    return EntryData[index];
                }

                var sir0 = entry.ToSir0();
                return Gyu0.Compress(sir0.Data);
            })));

            // Build the .bin file data
            entryPointers.Add(0);
            foreach (var data in compressedEntries)
            {
                // Write data to .bin and the pointer to .ent
                // Align data to 16 bytes
                var binData = data.ReadArray();
                bin.Write(binData, 0, binData.Length);
                var paddingLength = 16 - (bin.Length % 16);
                if (paddingLength != 16)
                {
                    bin.SetLength(bin.Length + paddingLength);
                    bin.Position = bin.Length;
                }
                entryPointers.Add((int)bin.Position);
            }

            // Build the .ent file data
            var ent = new byte[entryPointers.Count * sizeof(int)];
            for (int i = 0; i < entryPointers.Count; i++)
            {
                BitConverter.GetBytes(entryPointers[i]).CopyTo(ent, i * sizeof(int));
            }

            return (bin.ToArray(), ent);
        }

        public Entry?[] Entries { get; set; }
        public IReadOnlyBinaryDataAccessor[] EntryData { get; set; }

        /// <summary>Get a dungeon_balance entry. If `temporary` is true, the entry will not be cached.</summary>
        public Entry GetEntry(int index, bool temporary = false)
        {
            if (temporary)
            {
                return new Entry(EntryData[index], index);
            }
            if (Entries[index] == null)
            {
                Entries[index] = new Entry(EntryData[index], index);
            }
            return Entries[index]!;
        }

        public class Entry
        {
            public Entry(short floorCount)
            {
                FloorInfos = new FloorInfoEntry[floorCount];
                for (short i = 0; i < floorCount; i++)
                {
                    FloorInfos[i] = new FloorInfoEntry(i);
                }
            }

            public Entry(IReadOnlyBinaryDataAccessor accessor, int index)
            {
                var buffer = Gyu0.Decompress(accessor);

                Sir0 sir0 = new Sir0(buffer);
                var offsetHeader = sir0.SubHeader.ReadInt64(0x00);
                var offsetWildPokemon = sir0.SubHeader.ReadInt64(0x08);
                var offset3 = sir0.SubHeader.ReadInt64(0x10);
                var offset4 = sir0.SubHeader.ReadInt64(0x18);
                var lenHeader = offsetWildPokemon - offsetHeader;
                var lenWildPokemon = offset3 - offsetWildPokemon;
                var lenTrapWeights = offset4 - offset3;
                var len4 = sir0.SubHeaderOffset - offset4;

                var headerEntrySize = FloorInfoEntry.Size;
                var entryCount = lenHeader / headerEntrySize;
                FloorInfos = new FloorInfoEntry[entryCount];
                for (int i = 0; i < lenHeader / headerEntrySize; i++)
                {
                    FloorInfos[i] = new FloorInfoEntry(sir0.Data.Slice(offsetHeader + i * headerEntrySize, headerEntrySize));
                }

                if (lenWildPokemon > 0) WildPokemon = new WildPokemonInfo(sir0.Data.Slice(offsetWildPokemon, lenWildPokemon));
                if (lenTrapWeights > 0) TrapWeights = new TrapWeights(sir0.Data.Slice(offset3, lenTrapWeights));
                if (len4 > 0) Data4 = new DungeonBalanceDataEntry4(sir0.Data.Slice(offset4, len4));
            }

            public Sir0 ToSir0()
            {
                var sir0 = new Sir0Builder(8);

                var floorInfoPointer = sir0.Length;
                foreach (var floor in FloorInfos)
                {
                    sir0.Write(sir0.Length, floor.ToByteArray());
                }

                sir0.Align(16);
                var wildPokemonPointer = sir0.Length;
                if (WildPokemon != null)
                {
                    sir0.Write(sir0.Length, WildPokemon.ToSir0().Data.ReadArray());
                }

                sir0.Align(16);
                var trapWeightsPointer = sir0.Length;
                if (TrapWeights != null)
                {
                    sir0.Write(sir0.Length, TrapWeights.ToSir0().Data.ReadArray());
                }

                sir0.Align(16);
                var data4Pointer = sir0.Length;
                if (Data4 != null)
                {
                    sir0.Write(sir0.Length, Data4.ToSir0().Data.ReadArray());
                }

                // Write the content header
                sir0.Align(16);
                sir0.SubHeaderOffset = sir0.Length;
                sir0.WritePointer(sir0.Length, floorInfoPointer);
                sir0.WritePointer(sir0.Length, wildPokemonPointer);
                sir0.WritePointer(sir0.Length, trapWeightsPointer);
                sir0.WritePointer(sir0.Length, data4Pointer);
                return sir0.Build();
            }

            public FloorInfoEntry[] FloorInfos { get; set; }
            public WildPokemonInfo? WildPokemon { get; set; }
            public TrapWeights? TrapWeights { get; set; }
            public DungeonBalanceDataEntry4? Data4 { get; set; }
        }

        [DebuggerDisplay("{Index} : {Event}|{Short02}")]
        public class FloorInfoEntry
        {
            internal const int Size = 98;

            public FloorInfoEntry(short index)
            {
                Index = index;
                Event = "";
                Bytes5Ato61 = new byte[0x61 - 0x5A + 1];
            }

            public FloorInfoEntry(IReadOnlyBinaryDataAccessor data)
            {
                Index = data.ReadInt16(0x00);
                Short02 = data.ReadInt16(0x02);
                Event = data.ReadString(0x04, 32, Encoding.ASCII).Trim('\0');
                TurnLimit = data.ReadInt16(0x24);
                MinMoneyStackSize = data.ReadInt16(0x26);
                MaxMoneyStackSize = data.ReadInt16(0x28);
                DungeonMapDataInfoIndex = data.ReadInt16(0x2A);
                NameID = data.ReadByte(0x2C);
                Byte2D = data.ReadByte(0x2D);
                Byte2E = data.ReadByte(0x2E);
                Byte2F = data.ReadByte(0x2F);
                Short30 = data.ReadInt16(0x30);
                Short32 = data.ReadInt16(0x32);
                Byte34 = data.ReadByte(0x34);
                Byte35 = data.ReadByte(0x35);
                RoomCount = data.ReadByte(0x36);
                Byte37 = data.ReadByte(0x37);
                Byte38 = data.ReadByte(0x38);
                Byte39 = data.ReadByte(0x39);
                FloorItemSetIndex = data.ReadByte(0x3A);
                KecleonShopItemSetIndex = data.ReadByte(0x3B);
                PossibleItemSetIndex3C = data.ReadByte(0x3C);
                NormalTreasureBoxItemSetIndex = data.ReadByte(0x3D);
                MonsterHouseItemSetIndex = data.ReadByte(0x3E);
                DeluxeTreasureBoxItemSetIndex = data.ReadByte(0x3F);
                Byte40 = data.ReadByte(0x40);
                Byte41 = data.ReadByte(0x41);
                MinItemDensity = data.ReadByte(0x42);
                MaxItemDensity = data.ReadByte(0x43);
                BuriedItemSetIndex = data.ReadByte(0x44);
                MaxBuriedItems = data.ReadByte(0x45);
                Byte46 = data.ReadByte(0x46);
                StickyItemChance = data.ReadByte(0x47);
                KecleonShopChance = data.ReadByte(0x48);
                Byte49 = data.ReadByte(0x49);
                Byte4A = data.ReadByte(0x4A);
                MinTrapDensity = data.ReadByte(0x4B);
                MaxTrapDensity = data.ReadByte(0x4C);
                MinEnemyDensity = data.ReadByte(0x4D);
                MaxEnemyDensity = data.ReadByte(0x4E);
                Byte4F = data.ReadByte(0x4F);
                Byte50 = data.ReadByte(0x50);
                Byte51 = data.ReadByte(0x51);
                MysteryHouseChance = data.ReadByte(0x52);
                MysteryHouseSize = data.ReadByte(0x53);
                InvitationIndex = data.ReadByte(0x54);
                MonsterHouseChance = data.ReadByte(0x55);
                Byte56 = data.ReadByte(0x56);
                Byte57 = data.ReadByte(0x57);
                Byte58 = data.ReadByte(0x58);
                Weather = (DungeonStatusIndex)data.ReadByte(0x59);
                Bytes5Ato61 = data.ReadArray(0x5A, 0x61 - 0x5A + 1);
            }

            public byte[] ToByteArray()
            {
                var data = new byte[Size];

                using var accessor = new BinaryFile(data);
                accessor.WriteInt16(0x00, Index);
                accessor.WriteInt16(0x02, Short02);
                accessor.WriteString(0x04, Encoding.ASCII, Event);
                accessor.WriteInt16(0x24, TurnLimit);
                accessor.WriteInt16(0x26, MinMoneyStackSize);
                accessor.WriteInt16(0x28, MaxMoneyStackSize);
                accessor.WriteInt16(0x2A, DungeonMapDataInfoIndex);
                accessor.Write(0x2C, NameID);
                accessor.Write(0x2D, Byte2D);
                accessor.Write(0x2E, Byte2E);
                accessor.Write(0x2F, Byte2F);
                accessor.WriteInt16(0x30, Short30);
                accessor.WriteInt16(0x32, Short32);
                accessor.Write(0x34, Byte34);
                accessor.Write(0x35, Byte35);
                accessor.Write(0x36, RoomCount);
                accessor.Write(0x37, Byte37);
                accessor.Write(0x38, Byte38);
                accessor.Write(0x39, Byte39);
                accessor.Write(0x3A, FloorItemSetIndex);
                accessor.Write(0x3B, KecleonShopItemSetIndex);
                accessor.Write(0x3C, PossibleItemSetIndex3C);
                accessor.Write(0x3D, NormalTreasureBoxItemSetIndex);
                accessor.Write(0x3E, MonsterHouseItemSetIndex);
                accessor.Write(0x3F, DeluxeTreasureBoxItemSetIndex);
                accessor.Write(0x40, Byte40);
                accessor.Write(0x41, Byte41);
                accessor.Write(0x42, MinItemDensity);
                accessor.Write(0x43, MaxItemDensity);
                accessor.Write(0x44, BuriedItemSetIndex);
                accessor.Write(0x45, MaxBuriedItems);
                accessor.Write(0x46, Byte46);
                accessor.Write(0x47, StickyItemChance);
                accessor.Write(0x48, KecleonShopChance);
                accessor.Write(0x49, Byte49);
                accessor.Write(0x4A, Byte4A);
                accessor.Write(0x4B, MinTrapDensity);
                accessor.Write(0x4C, MaxTrapDensity);
                accessor.Write(0x4D, MinEnemyDensity);
                accessor.Write(0x4E, MaxEnemyDensity);
                accessor.Write(0x4F, Byte4F);
                accessor.Write(0x50, Byte50);
                accessor.Write(0x51, Byte51);
                accessor.Write(0x52, MysteryHouseChance);
                accessor.Write(0x53, MysteryHouseSize);
                accessor.Write(0x54, InvitationIndex);
                accessor.Write(0x55, MonsterHouseChance);
                accessor.Write(0x56, Byte56);
                accessor.Write(0x57, Byte57);
                accessor.Write(0x58, Byte58);
                accessor.Write(0x59, (byte)Weather);
                accessor.Write(0x5A, Bytes5Ato61);

                return data;
            }

            public short Index { get; set; }
            public short Short02 { get; set; }
            public string Event { get; set; }
            public short TurnLimit { get; set; }
            public short MinMoneyStackSize { get; set; }
            public short MaxMoneyStackSize { get; set; }
            public short DungeonMapDataInfoIndex { get; set; }

            // Index of the hash in the list at 0x4BAADE0 in the v1.0.2 executable
            // Might also be the list at 0x4BAAFEC, 0x4BAB1F8 or 0x4BAB404.
            public byte NameID { get; set; }
            public byte Byte2D { get; set; }
            public byte Byte2E { get; set; }
            public byte Byte2F { get; set; }
            public short Short30 { get; set; }
            public short Short32 { get; set; }
            public byte Byte34 { get; set; }
            public byte Byte35 { get; set; }
            public byte RoomCount { get; set; }
            public byte Byte37 { get; set; }
            public byte Byte38 { get; set; }
            public byte Byte39 { get; set; }
            public byte FloorItemSetIndex { get; set; }
            public byte KecleonShopItemSetIndex { get; set; }
            public byte PossibleItemSetIndex3C { get; set; }
            public byte NormalTreasureBoxItemSetIndex { get; set; }
            public byte MonsterHouseItemSetIndex { get; set; }
            public byte DeluxeTreasureBoxItemSetIndex { get; set; }
            public byte Byte40 { get; set; }
            public byte Byte41 { get; set; }
            public byte MinItemDensity { get; set; }
            public byte MaxItemDensity { get; set; }
            public byte BuriedItemSetIndex { get; set; }
            public byte MaxBuriedItems { get; set; }
            public byte Byte46 { get; set; }
            public byte StickyItemChance { get; set; }
            public byte KecleonShopChance { get; set; }
            public byte Byte49 { get; set; }
            public byte Byte4A { get; set; }
            public byte MinTrapDensity { get; set; }
            public byte MaxTrapDensity { get; set; }
            public byte MinEnemyDensity { get; set; }
            public byte MaxEnemyDensity { get; set; }
            public byte Byte4F { get; set; }
            public byte Byte50 { get; set; }
            public byte Byte51 { get; set; }
            public byte MysteryHouseChance { get; set; }
            public byte MysteryHouseSize { get; set; } // 0 = large, 1 = small
            public byte InvitationIndex { get; set; }
            public byte MonsterHouseChance { get; set; }
            public byte Byte56 { get; set; }
            public byte Byte57 { get; set; }
            public byte Byte58 { get; set; }
            public DungeonStatusIndex Weather { get; set; }
            public byte[] Bytes5Ato61 { get; set; }

        }

        public class WildPokemonInfo
        {
            // TODO: Dojo floor data is corrupted
            public WildPokemonInfo(IReadOnlyBinaryDataAccessor accessor)
            {
                var sir0 = new Sir0(accessor);

                int pokemonStatsCount = sir0.SubHeader.ReadInt32(0x00);
                int pokemonStatsOffset = sir0.SubHeader.ReadInt32(0x08);
                Stats = new StatsEntry[pokemonStatsCount];
                for (int i = 0; i < pokemonStatsCount; i++)
                {
                    var offset = sir0.Data.ReadInt64(pokemonStatsOffset + i * sizeof(long));
                    Stats[i] = new StatsEntry(i, sir0.Data.Slice(offset, 16));
                }

                int floorCount = sir0.SubHeader.ReadInt32(0x10);
                Floors = new FloorInfo[floorCount];
                for (int i = 0; i < floorCount; i++)
                {
                    Floors[i] = new FloorInfo(pokemonStatsCount);
                    var offset = sir0.SubHeader.ReadInt64(0x18 + i * sizeof(long));
                    for (int j = 0; j < pokemonStatsCount; j++)
                    {
                        Floors[i].Entries[j] = new FloorInfo.Entry(sir0.Data.Slice(offset + j * 16, 16));
                    }
                }
            }

            public WildPokemonInfo()
            {
                Stats = new StatsEntry[(int)CreatureIndex.END];
                for (int i = 0; i < (int)CreatureIndex.END; i++)
                {
                    Stats[i] = new StatsEntry();
                }

                Floors = new FloorInfo[99];
                for (int i = 0; i < 99; i++)
                {
                    Floors[i] = new FloorInfo((int)CreatureIndex.END);
                }
            }

            public Sir0 ToSir0()
            {
                var sir0 = new Sir0Builder(8);

                // Write the stats
                foreach (var stats in Stats)
                {
                    stats.Pointer = sir0.Length;
                    sir0.Write(sir0.Length, stats.ToByteArray());
                }

                // Write the stats pointers
                var statsPointer = sir0.Length;
                foreach (var stats in Stats)
                {
                    sir0.WritePointer(sir0.Length, stats.Pointer);
                }

                // Write the floor infos
                foreach (var floor in Floors)
                {
                    floor.Pointer = sir0.Length;
                    foreach (var floorEntry in floor.Entries)
                    {
                        sir0.Write(sir0.Length, floorEntry.ToByteArray());
                    }
                    sir0.WritePadding(sir0.Length, 16, 0xFF);
                }

                // Write the content header
                sir0.Align(16);
                sir0.SubHeaderOffset = sir0.Length;
                sir0.WriteInt64(sir0.Length, Stats.Length);
                sir0.WritePointer(sir0.Length, statsPointer);
                sir0.WriteInt64(sir0.Length, Floors.Length);
                foreach (var floor in Floors)
                {
                    sir0.WritePointer(sir0.Length, floor.Pointer);
                }
                return sir0.Build();
            }

            [DebuggerDisplay("{Index} : {XPYield}|{HitPoints}|{Attack}|{Defense}|{SpecialAttack}|{SpecialDefense}|{Speed}|{Level}")]
            public class StatsEntry
            {
                public StatsEntry(int index, IReadOnlyBinaryDataAccessor accessor)
                {
                    Index = index;
                    CreatureIndex = (CreatureIndex)(index + 1);
                    XPYield = accessor.ReadInt32(0x00);
                    HitPoints = accessor.ReadInt16(0x04);
                    Attack = accessor.ReadByte(0x06);
                    SpecialAttack = accessor.ReadByte(0x07);
                    Defense = accessor.ReadByte(0x08);
                    SpecialDefense = accessor.ReadByte(0x09);
                    Speed = accessor.ReadByte(0x0A);
                    StrongFoe = accessor.ReadByte(0x0B);
                    Level = accessor.ReadByte(0x0C);
                }

                public StatsEntry() { }

                public byte[] ToByteArray()
                {
                    var data = new byte[16];

                    using var accessor = new BinaryFile(data);
                    accessor.WriteInt32(0x00, XPYield);
                    accessor.WriteInt16(0x04, HitPoints);
                    accessor.Write(0x06, Attack);
                    accessor.Write(0x07, SpecialAttack);
                    accessor.Write(0x08, Defense);
                    accessor.Write(0x09, SpecialDefense);
                    accessor.Write(0x0A, Speed);
                    accessor.Write(0x0B, StrongFoe);
                    accessor.Write(0x0C, Level);

                    return data;
                }

                public int Index { get; set; }
                public CreatureIndex CreatureIndex { get; set; }
                public int XPYield { get; set; }
                public short HitPoints { get; set; }
                public byte Attack { get; set; }
                public byte SpecialAttack { get; set; }
                public byte Defense { get; set; }
                public byte SpecialDefense { get; set; }
                public byte Speed { get; set; }
                public byte StrongFoe { get; set; }
                public byte Level { get; set; }

                public long Pointer { get; set; }
            }

            public class FloorInfo
            {
                public FloorInfo(int entryCount)
                {
                    Entries = new Entry[entryCount];
                    for (int i = 0; i < entryCount; i++)
                    {
                        Entries[i] = new Entry();
                    }
                }

                public FloorInfo()
                {
                    Entries = new Entry[0];
                }

                [DebuggerDisplay("{PokemonIndex} : {SpawnRate}|{RecruitmentLevel}|{Byte0B}")]
                public class Entry
                {
                    public Entry(IReadOnlyBinaryDataAccessor accessor)
                    {
                        PokemonIndex = accessor.ReadInt16(0x00);
                        SpawnRate = accessor.ReadByte(0x02);
                        Byte03 = accessor.ReadByte(0x03);
                        Byte04 = accessor.ReadByte(0x04);
                        Byte05 = accessor.ReadByte(0x05);
                        Byte06 = accessor.ReadByte(0x06);
                        Byte07 = accessor.ReadByte(0x07);
                        Byte08 = accessor.ReadByte(0x08);
                        Byte09 = accessor.ReadByte(0x09);
                        RecruitmentLevel = accessor.ReadByte(0x0A);
                        Byte0B = accessor.ReadByte(0x0B);
                        Byte0C = accessor.ReadByte(0x0C);
                        Byte0D = accessor.ReadByte(0x0D);
                        Byte0E = accessor.ReadByte(0x0E);
                        Byte0F = accessor.ReadByte(0x0F);
                    }

                    public Entry() { }

                    public byte[] ToByteArray()
                    {
                        var data = new byte[16];

                        using var accessor = new BinaryFile(data);
                        accessor.WriteInt16(0x00, PokemonIndex);
                        accessor.Write(0x02, SpawnRate);
                        accessor.Write(0x03, Byte03);
                        accessor.Write(0x04, Byte04);
                        accessor.Write(0x05, Byte05);
                        accessor.Write(0x06, Byte06);
                        accessor.Write(0x07, Byte07);
                        accessor.Write(0x08, Byte08);
                        accessor.Write(0x09, Byte09);
                        accessor.Write(0x0A, RecruitmentLevel);
                        accessor.Write(0x0B, Byte0B);
                        accessor.Write(0x0C, Byte0C);
                        accessor.Write(0x0D, Byte0D);
                        accessor.Write(0x0E, Byte0E);
                        accessor.Write(0x0F, Byte0F);

                        return data;
                    }

                    public short PokemonIndex { get; set; }
                    public byte SpawnRate { get; set; }
                    public byte Byte03 { get; set; }
                    public byte Byte04 { get; set; }
                    public byte Byte05 { get; set; }
                    public byte Byte06 { get; set; }
                    public byte Byte07 { get; set; }
                    public byte Byte08 { get; set; }
                    public byte Byte09 { get; set; }
                    public byte RecruitmentLevel { get; set; }
                    public byte Byte0B { get; set; }
                    public byte Byte0C { get; set; }
                    public byte Byte0D { get; set; }
                    public byte Byte0E { get; set; }
                    public byte Byte0F { get; set; }
                }

                public Entry[] Entries { get; set; }

                public long Pointer { get; set; }
            }

            public StatsEntry[] Stats { get; set; }
            public FloorInfo[] Floors { get; set; }
        }

        /// <summary>
        /// Trap weights, starting at ItemIndex.TRAP_MIN
        /// </summary>
        public class TrapWeights
        {
            public TrapWeights(IReadOnlyBinaryDataAccessor accessor)
            {
                var sir0 = new Sir0(accessor);
                int count = sir0.SubHeader.ReadInt32(0x00);
                Records = new Record[count];
                for (int i = 0; i < count; i++)
                {
                    Records[i] = new Record();
                    var offset = sir0.SubHeader.ReadInt64(0x08 + i * sizeof(long));
                    for (int j = 0; j < Records[i].Entries.Length; j++)
                    {
                        Records[i].Entries[j] = new Record.Entry(sir0.Data.Slice(offset + j * 8, 8));
                    }
                }
            }

            public TrapWeights()
            {
                Records = new Record[99];
                for (int i = 0; i < 99; i++)
                {
                    Records[i] = new Record();
                }
            }

            public Sir0 ToSir0()
            {
                var sir0 = new Sir0Builder(8);

                // Write the records
                foreach (var record in Records)
                {
                    record.Pointer = sir0.Length;
                    sir0.Write(sir0.Length, record.ToByteArray());
                }

                // Write the content header
                sir0.Align(16);
                sir0.SubHeaderOffset = sir0.Length;
                sir0.WriteInt64(sir0.Length, Records.Length);
                foreach (var record in Records)
                {
                    sir0.WritePointer(sir0.Length, record.Pointer);
                }
                return sir0.Build();
            }

            public class Record
            {
                public Record()
                {
                    Entries = new Entry[33];
                    for (int i = 0; i < 33; i++)
                    {
                        Entries[i] = new Entry();
                    }
                }

                public byte[] ToByteArray()
                {
                    var data = new byte[Entries.Length * 8];
                    for (int i = 0; i < Entries.Length; i++)
                    {
                        Entries[i].ToByteArray().CopyTo(data, i * 8);
                    }
                    return data;
                }

                [DebuggerDisplay("{Index} : {Weight}|{Int04}")]
                public class Entry
                {
                    public Entry(IReadOnlyBinaryDataAccessor accessor)
                    {
                        Index = accessor.ReadInt16(0x00);
                        Weight = accessor.ReadInt16(0x02);
                        Int04 = accessor.ReadInt16(0x04);
                    }

                    public Entry() { }

                    public byte[] ToByteArray()
                    {
                        var data = new byte[8];
                        BitConverter.GetBytes(Index).CopyTo(data, 0x00);
                        BitConverter.GetBytes(Weight).CopyTo(data, 0x02);
                        BitConverter.GetBytes(Int04).CopyTo(data, 0x04);
                        return data;
                    }

                    public short Index { get; set; }
                    public short Weight { get; set; }
                    public int Int04 { get; set; }  // all 0s
                }

                public Entry[] Entries { get; set; }

                public long Pointer { get; set; }
            }

            public Record[] Records { get; set; }
        }

        public class DungeonBalanceDataEntry4
        {
            public DungeonBalanceDataEntry4(IReadOnlyBinaryDataAccessor accessor)
            {
                var sir0 = new Sir0(accessor);
                int count = sir0.SubHeader.ReadInt32(0x00);
                Records = new Record[count];
                for (int i = 0; i < count; i++)
                {
                    Records[i] = new Record();
                    var offset = sir0.SubHeader.ReadInt64(0x08 + i * sizeof(long));
                    for (int j = 0; j < Records[i].Entries.Length; j++)
                    {
                        Records[i].Entries[j] = new Record.Entry(sir0.Data.Slice(offset + j * 8, 8));
                    }
                }
            }

            public DungeonBalanceDataEntry4()
            {
                Records = new Record[45];
                for (int i = 0; i < 45; i++)
                {
                    Records[i] = new Record();
                }
            }

            public Sir0 ToSir0()
            {
                var sir0 = new Sir0Builder(8);

                // Write the records
                foreach (var record in Records)
                {
                    record.Pointer = sir0.Length;
                    sir0.Write(sir0.Length, record.ToByteArray());
                }

                // Write the content header
                sir0.Align(16);
                sir0.SubHeaderOffset = sir0.Length;
                sir0.WriteInt64(sir0.Length, Records.Length);
                foreach (var record in Records)
                {
                    sir0.WritePointer(sir0.Length, record.Pointer);
                }
                return sir0.Build();
            }


            public class Record
            {
                public Record()
                {
                    Entries = new Entry[46];
                    for (int i = 0; i < 46; i++)
                    {
                        Entries[i] = new Entry();
                    }
                }

                public byte[] ToByteArray()
                {
                    var data = new byte[Entries.Length * 8];
                    for (int i = 0; i < Entries.Length; i++)
                    {
                        Entries[i].ToByteArray().CopyTo(data, i * 8);
                    }
                    return data;
                }

                [DebuggerDisplay("{Short00}|{Short02}|{Int04}")]
                public class Entry
                {
                    public Entry(IReadOnlyBinaryDataAccessor accessor)
                    {
                        Short00 = accessor.ReadInt16(0x00);
                        Short02 = accessor.ReadInt16(0x02);
                        Int04 = accessor.ReadInt16(0x04);
                    }

                    public Entry() { }

                    public byte[] ToByteArray()
                    {
                        var data = new byte[8];
                        BitConverter.GetBytes(Short00).CopyTo(data, 0x00);
                        BitConverter.GetBytes(Short02).CopyTo(data, 0x02);
                        BitConverter.GetBytes(Int04).CopyTo(data, 0x04);
                        return data;
                    }

                    public short Short00 { get; set; }  // 0 through 45, skipping 13
                    public short Short02 { get; set; }  // all 60s
                    public int Int04 { get; set; }      // all 0s
                }

                public Entry[] Entries { get; set; }

                public long Pointer { get; set; }
            }

            public Record[] Records { get; set; }
        }
    }
}
