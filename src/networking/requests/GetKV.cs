﻿using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
namespace Listener
{
    class GetKV
    {
        [DllImport("nokvhash.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern string StringHash(List<KVS> kvs);

        private enum eGetKVstatus
        {          
            RESPONSE_SUCCESS = 1,

            // kv
            RESPONSE_KV_NEW_ALLOCATED,
            RESPONSE_KV_TIMEOUT,
            RESPONSE_ERROR,
        };

        static int Lowest(params int[] inputs)
        {
            int lowest = inputs[0];
            foreach (var input in inputs)
                if (input < lowest) lowest = input;
            return lowest;
        }

        private static string CalculateBestKV(string missThisOne = "00000000")
        {
            List<KVS> kvs = MySQL.GetKVs();


            if (kvs.Count != 0)
            {
                if (missThisOne != "00000000")
                {
                    for (int i = 0; i < kvs.Count; i++)
                    {
                        if (kvs[i].strHash == missThisOne)
                        {
                            kvs.RemoveAt(i);
                            break;
                        }
 
                    }
                }

                kvs.Sort((x, y) => x.iUses.CompareTo(y.iUses));/*needs work*/

                //string returnhash = StringHash(kvs);
                //Array.Reverse(returnhash);

                //Lowest(kvs[0].iUses);

                return kvs[0].strHash;
            }

            return "00000000";
        }


        public static void Handle(EndianReader reader, EndianWriter serverWriter, Header header, List<Log.PrintQueue> logId, string ip)
        {
            Log.Add(logId, ConsoleColor.Blue, "Command", "PacketGetKV", ip);
            Log.Add(logId, ConsoleColor.Cyan, "Console Key", Utils.BytesToString(header.szConsoleKey), ip);

            // the buffer that the resp is written into
            byte[] resp = new byte[32 + 0x732 + Global.iEncryptionStructSize];

            EndianWriter writer = new EndianWriter(new MemoryStream(resp), EndianStyle.BigEndian);

            eGetKVstatus status = eGetKVstatus.RESPONSE_ERROR;

            byte[] hashA = new byte[0x4];
            byte[] consoleObfuscationKey = new byte[0x10];
            byte[] consolePrivateKey = new byte[0x1D0];
            byte[] xeIkaPrivateKey = new byte[0x390];
            byte[] consoleSerial = new byte[0xC];
            byte[] consoleCert = new byte[0x1A8];
            byte[] macAddress = new byte[0x6];

            int hashFindTimeout = 0;
            string ignoreThisHash = "00000000";
            bool refresh = reader.ReadBoolean();
            bool cond = (reader.ReadUInt32() & 0xF0000000) > 0x40000000;

            bool kvbanned = false;
            KVStats stats = new KVStats();
            MySQL.UpdateUsingKVEndpointStatus(Utils.BytesToString(header.szToken), true);



            ClientInfo client = new ClientInfo();
            if (MySQL.GetClientData(Utils.BytesToString(header.szConsoleKey), ref client))
            {

                if (client.iTimeEnd < Utils.GetTimeStamp() && client.iReserveSeconds == 0 && !Global.bFreemode)
                {
                    status = eGetKVstatus.RESPONSE_ERROR;
                    Log.Add(logId, ConsoleColor.DarkYellow, "Info", "PacketGetKV: Client doesn't have any time", ip);
                    goto end;
                }

                generate:

                //File.WriteAllText("Currenthash1.txt", client.strNoKVHash);
                if (client.strNoKVHash == "00000000" || refresh)
                {
                    // ALLOCATE A KV
                    if (refresh)
                    {
                        // if it's not 0 and if it's been less than 6 hours
                        if (client.iNoKVLastRefresh != 0 && (Utils.GetTimeStamp() - client.iNoKVLastRefresh) < 21600)
                        {
                            Log.Add(logId, ConsoleColor.DarkYellow, "Info", "PacketGetKV: Trying to refresh KV too fast", ip);
                            status = eGetKVstatus.RESPONSE_KV_TIMEOUT;
                            goto end;
                        }

                        ignoreThisHash = client.strNoKVHash;
                    }

                    hashFindTimeout++;
                    string hash = CalculateBestKV(ignoreThisHash);

                    //File.WriteAllText("Currenthash2.txt", hash);
                    string locationb = "Server Data/KVs/" + hash.ToString() + "/kV.bin";
                    if (!File.Exists(locationb))
                    {

                        if (hashFindTimeout == 10)
                        {
                            Log.Add(logId, ConsoleColor.DarkYellow, "Info", string.Format("PacketGetKV", "Failed to generate a KV hash, no directories existed: {0} ", locationb), ip);
                            status = eGetKVstatus.RESPONSE_ERROR;
                            goto end;
                        }

                        ignoreThisHash = hash;

                        goto generate;
                    }
                    //File.WriteAllText("Currenthash.txt", hash);
                    // assume it has a good kv hash now that exists
                    MySQL.UpdateClientNoKVHash(Utils.BytesToString(header.szConsoleKey), hash);
                    MySQL.IncrementKVUsesCount(hash);
                    client.strNoKVHash = hash;
                    status = eGetKVstatus.RESPONSE_KV_NEW_ALLOCATED;
                    if (refresh)
                    {
                        // update last time
                        MySQL.UpdateClientNoKVLastRefresh(Utils.BytesToString(header.szConsoleKey));

                        if (ignoreThisHash != "00000000")
                        {
                            // negate uses
                            MySQL.DecrementKVUsesCount(ignoreThisHash);
                        }
                    }

                    goto finalloop;
                }
                else
                {
                    //finalloop:
                    //HAS A KV
                    string locationA = "Server Data/KVs/" + client.strNoKVHash + "/kV.bin";

                    if (File.Exists(locationA))
                    {

                        goto finalloop;

                    }
                    else
                    {
                        Log.Add(logId, ConsoleColor.DarkYellow, "Info", string.Format("PacketGetKV directory doesn't exist, allocating a new KV", client.strNoKVHash.ToString()), ip);

                        goto generate;
                    }
                }
            }

            finalloop:
            byte[] KVfile = new byte[0];
            int fileSize = 0;
            string location = "Server Data/KVs/" + client.strNoKVHash + "/kV.bin";
            KVfile = File.ReadAllBytes(location);
            fileSize = KVfile.Length;

            if (fileSize < 0xB80)
            {
                Log.Add(logId, ConsoleColor.DarkYellow, "Info", string.Format("PacketGetKV {0} size is below 0xB80", client.strNoKVHash.ToString()), ip);
                status = eGetKVstatus.RESPONSE_ERROR;
                goto end;
            }

            if (File.Exists(location))
            {

                Buffer.BlockCopy(KVfile.Skip(0x4).Take(0x4).ToArray(), 0x0, hashA, 0x0, 0x4);
                Buffer.BlockCopy(KVfile.Skip(0xD0).Take(0x10).ToArray(), 0x0, consoleObfuscationKey, 0x0, 0x10);
                Buffer.BlockCopy(KVfile.Skip(0x298).Take(0x1D0).ToArray(), 0x0, consolePrivateKey, 0x0, 0x1D0);
                Buffer.BlockCopy(KVfile.Skip(0x468).Take(0x390).ToArray(), 0x0, xeIkaPrivateKey, 0x0, 0x390);
                Buffer.BlockCopy(KVfile.Skip(0xB0).Take(0xC).ToArray(), 0x0, consoleSerial, 0x0, 0xC);
                Buffer.BlockCopy(KVfile.Skip(0x9C8).Take(0x1A8).ToArray(), 0x0, consoleCert, 0x0, 0x1A8);
                byte[] ConsoleID = KVfile.Skip(0x9CA).Take(5).ToArray();
                byte[] MACAddress = { 0x00, 0x22, 0x48, (byte)(((ConsoleID[1] << 4) & 0xF0) | ((ConsoleID[2] >> 4) & 0xF)), (byte)(((ConsoleID[2] << 4) & 0xF0) | ((ConsoleID[3] >> 4) & 0xF)), (byte)(((ConsoleID[3] << 4) & 0xF0) | ((ConsoleID[4] >> 4) & 0xF)) };
                
                Buffer.BlockCopy(MACAddress, 0, macAddress, 0, 0x6);

             
                // encrypt the cert so it isn't in our ram
                byte[] rc4Key = new byte[] { 0x70, 0x6C, 0x7A, 0x20, 0x64, 0x6F, 0x6E, 0x27, 0x74, 0x20, 0x73, 0x74, 0x65, 0x61, 0x6C, 0x20, 0x61, 0x6E,
                    0x64, 0x20, 0x62, 0x61, 0x6E, 0x20, 0x6B, 0x76, 0x2C, 0x20, 0x69, 0x73, 0x20, 0x73, 0x69, 0x6E };

                Security.RC4(ref consoleCert, rc4Key);
                //File.WriteAllBytes("ConsoleCert.bin", consoleCert);

                if (status != eGetKVstatus.RESPONSE_KV_NEW_ALLOCATED)
                {
                    status = eGetKVstatus.RESPONSE_SUCCESS;
                }
                //File.WriteAllText("Currenthash2.txt", Utils.BytesToString(hashA));
                


                /*Update KV User hash*/
                MySQL.UpdateUserInfoWelcomePacket(Utils.BytesToString(header.szConsoleKey), Utils.BytesToString(hashA), ip);
                
                
                /*Update KV Stats hash*/
                if (MySQL.GetKVStats(Utils.BytesToString(hashA), ref stats))
                {
                    // update shit
                    //MySQL.UpdateKVStat(Utils.BytesToString(hashA), kvbanned);
                }
                else
                {
                    MySQL.AddKVStat(Utils.BytesToString(hashA), (int)Utils.GetTimeStamp(), (int)Utils.GetTimeStamp(), kvbanned, kvbanned ? (int)Utils.GetTimeStamp() : 0);
                }


                Log.Add(logId, ConsoleColor.Green, "Info", string.Format("Streaming Client NoKVHash: {0}", client.strNoKVHash.ToString()), ip);

            }
            else
            {
                Log.Add(logId, ConsoleColor.DarkYellow, "Info", string.Format("PacketGetKV {0} can't be opened", client.strNoKVHash.ToString()), ip);

                status = eGetKVstatus.RESPONSE_ERROR;
                goto end;
            }


            end:
            Security.EncryptionStruct enc = new Security.EncryptionStruct();
            Security.GenerateKeys(ref enc);
            Security.EncryptHash(ref enc, header);
            Security.EncryptKeys(ref enc);

            /*Send Security Shit*/
            writer.Write(header.szRandomKey);
            writer.Write(header.szRC4Key);
            writer.Write(enc.iKey1);
            writer.Write(enc.iKey2);
            writer.Write(enc.iHash);


            /*Send KV Stuff*/
            writer.Write((int)status);
            writer.Write(hashA);
            writer.Write(consoleObfuscationKey);
            writer.Write(consolePrivateKey);
            writer.Write(xeIkaPrivateKey);
            writer.Write(consoleSerial);
            writer.Write(consoleCert);
            writer.Write(macAddress);

            writer.Close();
            //Log.Add(logId, ConsoleColor.Green, "Info", string.Format("NoKVHash: {0}" + Utils.BytesToString(hashA)), ip);
            //Log.Add(logId, ConsoleColor.Magenta, "Info", string.Format("RC4 Key: {0}", Utils.BytesToString(header.szRC4Key)), ip);
            Log.Add(logId, ConsoleColor.Green, "Status", "Response sent", ip);
            Log.Print(logId);
            Security.SendPacket(serverWriter, header, resp, enc);
        }
    }
}
