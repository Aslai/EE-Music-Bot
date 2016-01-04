/*
 * EE Midi Bot - Upload midi levels to Everybody Edits.
 * Copyright (C) 2016 Andrew Story
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using PlayerIOClient;
using System.IO;
using System.Collections.Generic;

namespace EEMidiBot
{
	class Midi{
		//Simple storage class for event information
		public class Event{
			public long Time = 0;
			public long Delay = 0;
			public byte Status = 0;
			public byte Arg1 = 0;
			public byte Arg2 = 0;
			public byte[] Data = null;
			public bool Valid{
				get {
					return (Status >> 4) >= 8; 
				}
			}
			public Event Copy(){
				Event e = new Event ();
				e.Time = Time;
				e.Delay = Delay;
				e.Status = Status;
				e.Arg1 = Arg1;
				e.Arg2 = Arg2;
				e.Data = Data;
				return e;
			}
		}

		private class Track{
			private List<Event> Events = new List<Event>();
			//Used for tracking where we are in time
			private int OldIndex = 0;
			public void PushEvent(int delay, byte status, byte arg1, byte arg2, byte[] data = null){
				Event e = new Event ();
				e.Delay = delay;
				e.Status = status;
				e.Arg1 = arg1;
				e.Arg2 = arg2;
				e.Time = delay;
				e.Data = data;
				if (Events.Count > 0) {
					e.Time += Events [Events.Count - 1].Time;
				}
				Events.Add(e);
			}
			public void Rewind(){
				OldIndex = 0;
			}
			public Event GetNext(){
				if (OldIndex >= Events.Count) {
					return new Event();
				}
				return Events [OldIndex];
			}
			//Indicate externally that we actually want to advance the counter, just in case another track got priority
			public void Consume(){
				OldIndex ++;
			}
		}
		private List<Track> MyTracks;
		private long Speed;
		private int Tracks;
		private int SubFormat;
		private string MyLastError;
		public string LastError{
			get {
				return MyLastError;
			}
		}
		public long TicksPerQuarter{
			get {
				return Speed;
			}
		}
		public int Format{
			get{
				return SubFormat;
			}
		}

		public Midi(){
			OldTime = 0;
			Speed = 128;
			Tracks = 0;
			SubFormat = 0;
			MyLastError = "";
			MyTracks = new List<Track> ();
			IsFinished = false;
		}

		//Helper function to compare a part of the byte array with an ASCII string
		private bool MatchBytes(ref byte[] data, int offset, string compare){
			foreach( char c in compare ){
				if ((byte)c != data [offset++]) {
					return false;
				}
			}
			return true;
		}

		private int ReadDuration(ref byte[] data, ref int offset){
			//Technically there's supposed to be a limit.
			//Each byte carries 7 bits of information and one bit of carry.
			//If the carry bit is set, assume there really is more data to read.
			int ret = 0;
			while (offset < data.Length) {
				byte value = data [offset++];
				ret = (ret << 7) + (value & 0x7F);
				if ((value & 0x80) == 0) {
					break;
				}
			}
			return ret;
		}

		private int ReadTrack(ref byte[] data, int offset){
			if (data.Length == offset) {
				return 0;
			}
			if (data.Length < offset + 8) {
				MyLastError = "Garbage found at end of file";
				return 0;
			}
			if (!MatchBytes (ref data, offset, "MTrk")) {
				MyLastError = "Invalid Midi file (Expected MTrk not found)";
				return -1;
			}
			int trackLength = (data [offset + 4] << 24) + (data [offset + 5] << 16) + (data [offset + 6] << 8) + data [offset + 7];
			offset += 8;
			if (offset + trackLength > data.Length) {
				MyLastError = "Invalid Midi file (Unexpected EOF)";
				return -1;
			}
			trackLength += offset;
			Track output = new Track();
			byte priorStatus = 0;
			int bytes = 0;
			while (offset < data.Length && offset < trackLength) {
				int length = ReadDuration (ref data, ref offset);
				if (offset + 2 >= data.Length) {
					MyLastError = "Invalid Midi file (Unexpected EOF)";
					return -1;
				}
				byte arg1 = 0, arg2 = 0;
				byte status = data [offset++];
				if ((status >> 4) < 8) {
					//Deal with running status
					if (priorStatus == 0) {
						MyLastError = "Invalid Midi file (Unrecognized status code) " + offset.ToString ();
						return -1;
					}
					arg1 = status;
					if (bytes == 2) {
						arg2 = data [offset++];
					}
					status = priorStatus;
				} else {
					if ((status >> 4) < 12 || (status >> 4) > 14) {
						bytes = 2;
					} else {
						bytes = 1;
					}

					arg1 = data [offset++];
					if (bytes == 2) {
						arg2 = data [offset++];
					}
				}
				priorStatus = status;
				if (priorStatus >= 0xF0) {
					priorStatus = 0;
				}
				byte[] edata = null;
				if (status == 0xFF) {
					if (offset + arg2 <= data.Length) {
						//Extract substring of bytes
						edata = new byte[arg2];
						for (int i = 0; i < arg2; ++i) {
							edata [i] = data [offset++];
						}
					}
				}
				output.PushEvent (length, status, arg1, arg2, edata);
			}
			MyTracks.Add (output);
			return offset;
		}

		public bool Read(string fname){
			byte[] data = File.ReadAllBytes (fname);
			//Don't bother with exception passing, because that's silly... Right?
			if (data.Length < 14) {
				MyLastError = "Invalid Midi file (Too small)";
				return false;
			}
			if (!MatchBytes (ref data, 0, "MThd")) {
				MyLastError = "Invalid Midi file (no MThd at start)";
				return false;
			}
			int size = (data [4] << 24) + (data [5] << 16) + (data [6] << 8) + (data [7]);
			if (size != 6) {
				MyLastError = "Unsupported Midi file (Header is too long) " + Convert.ToString(size);
				return false;
			}
			SubFormat = (data [8] << 8) + data [9];
			Tracks = (data [10] << 8) + data [11];
			Speed = (data [12] << 8) + data [13];
			int offset = 14;
			int tracksFound = 0;
			while (true) {
				int result = ReadTrack(ref data, offset);
				//A status of 0 is end of file, -1 is an error, positive means continue reading.
				if (result < 0) {
					return false;
				}
				if (result == 0) {
					break;
				}
				offset = result;
				tracksFound++;
			}
			if (Tracks > tracksFound) {
				MyLastError = "Invalid Midi file (More tracks were declared than do exist)";
				return false;
			}
			return true;
		}
		
		private long OldTime;
		public void Rewind(){
			foreach (Track t in MyTracks) {
				t.Rewind ();
			}
			OldTime = 0;
			IsFinished = false;
		}

		//Iterate over all the tracks to find the next event that should be dealt with.
		public Event NextEvent(){
			long newTime = 0x7FFFFFFF;
			Event ret = new Event ();
			Track whichTrack = new Track ();
			foreach (Track t in MyTracks) {
				Event e = t.GetNext ();
				if (e.Valid && e.Time < newTime && e.Time >= OldTime ) {
					ret = e;
					whichTrack = t;
					newTime = e.Time;
				}
			}
			if (ret.Status != 0) {
				ret = ret.Copy ();
				ret.Delay = ret.Time - OldTime;
				OldTime = ret.Time;
			} else {
				IsFinished = true;
			}
			whichTrack.Consume ();
			return ret;
		}

		private bool IsFinished;
		public bool Finished{
			get{
				return IsFinished;
			}
		}
	}
	class MainClass
	{
		//Convert the world key to something usable
		static string derot(string worldkey)
		{
			string newKey = "";
			foreach( char C in worldkey ){
				int c = C;
				if ((c >= 'a' && c <= 'm') || (c >= 'A' && c <= 'M')) {
					c += 13;
				}
				else if ((c > 'm' && c <= 'z') || (c > 'M' && c <= 'Z')) {
					c -= 13;
				}
				newKey += (char)c;
			}
			return newKey;
		}

		private static string WorldKey = "";
		//private static int WorldWidth = 0;
		private static int WorldHeight = 0;

		
		private static void onMessage(object sender, PlayerIOClient.Message m)
		{
			//Init is the only event we really care about for this bot.
			switch (m.Type) {
				case "init":
				{
					WorldKey = derot (m.GetString (5));
					//WorldWidth = m.GetInt (17);
					WorldHeight = m.GetInt (18);
					return;
				}
			}
		}

		//A quick and dirty map from MIDI percussion to EE percussion
		private static int[] percussion = {
			0, 13, 3, 2, 7, 2, 12, 6, 13, 14,
			11, 10, 8, 10, 17, 17, 19, 9, 18,
			19, 16, 1, 18, 10, 11, 10, 10, 11,
			10, 11, 19, 19, 9, 9, 10, 10, 10,
			10, 19, 19, 19, 19, 19, 6, 17
		};

		private static void Write(PlayerIOClient.Connection con, Midi m){
			long x = 1;
			long y = 2;
			long offset = 0;
			int distance = WorldHeight - 6;
			int id = 1;
			long xprev = 1;
			long tempo = 60000000 / 120; //Default microseconds per beat (120 bpm)
			while (true) {
				Midi.Event e = m.NextEvent ();
				if (!e.Valid) {
					break;
				}
				if (e.Status == 0xFF) {
					//On meta event, I only really care about tempo here.
					if (e.Arg1 == 0x51 && e.Arg2 == 3) {
						tempo = (e.Data [0] << 16 ) + (e.Data[1] << 8 ) + e.Data[2];
					}
				}
				if ((e.Status >> 4) == 9) {
					int note = e.Arg1;
					for (int i = 3; i < e.Arg2; i += 85) { 	//If the note volume is 3 or less, don't emit a note.
															//If the note volume is 88 or higher, emit two notes.
						//e.Time is in ticks, tempo is in microseconds per tick.
						//e.Time * tempo / m.TicksPerQuarter = the event time in microseconds.
						//Divide that by how long each block should represent (I use 1/90th of a second)
						if (e.Time * tempo / m.TicksPerQuarter / (1000000L / 90L) <= offset) {
							offset++;
						} else {
							offset = e.Time * tempo / m.TicksPerQuarter / (1000000L / 90L);
						}

						//Calculate the X/Y coordinates based on time. This does NOT account for time spent in portals.
						//Someone whould probably fix that.
						x = offset / distance + 2;
						y = offset % distance + 3;

						//If the channel is 9 (AKA MIDI channel 10)
						if ((e.Status & 0xF) == 9) {
							//Deal with drums
							if (note >= 35 && note <= 81) {
								con.Send (WorldKey, 0, x, y, 83, percussion[note - 35]);
								System.Threading.Thread.Sleep (100);
								break;
							}
						} else {
							//C3 is note 36 in MIDI, and note 0 in EE
							con.Send (WorldKey, 0, x, y, 77, note - 48);
							System.Threading.Thread.Sleep (100);
						}
					}
				}
				//If there's space missing portals...
				if (x > xprev) {
					for (long i = xprev; i < x; ++i) {
						//Add in portals to the slack space
						con.Send (WorldKey, 0, i, 2, 242, 1, id, id - 1);
						System.Threading.Thread.Sleep (100);
						con.Send (WorldKey, 0, i, WorldHeight - 3, 242, 1, id + 1, id + 2);
						System.Threading.Thread.Sleep (100);
						id += 2;
					}
				}
				xprev = x;
			}

			//Fill in the last column of portals
			con.Send (WorldKey, 0, x, 2, 242, 1, id, id - 1);
			System.Threading.Thread.Sleep (100);
			con.Send (WorldKey, 0, x, WorldHeight - 3, 242, 1, id + 1, 1);
		}

		private static PlayerIOClient.Client DoLogin (int method){
			PlayerIOClient.Client client = null;
			switch (method) {
				case 1: {
					Console.Write ("Username: ");
					string name = Console.ReadLine ();

					Console.Write ("Password: ");
					string pass = Console.ReadLine ();

					client = PlayerIO.QuickConnect.SimpleConnect ("everybody-edits-su9rn58o40itdbnw69plyw", name, pass, new string[0]);
				} break;
				case 2: {
					Console.Write ("OAuth Token: ");
					string name = Console.ReadLine ();

					client = PlayerIO.QuickConnect.FacebookOAuthConnect ("everybody-edits-su9rn58o40itdbnw69plyw", name, "", new string[0]);
				} break;
				case 3: {
					Console.Write ("User ID: ");
					string name = Console.ReadLine ();

					Console.Write ("Auth Token: ");
					string pass = Console.ReadLine ();

					client = PlayerIO.QuickConnect.KongregateConnect ("everybody-edits-su9rn58o40itdbnw69plyw", name, pass, new string[0]);
				} break;
			default:
				throw new InvalidDataException ("Unknown login method for input \"" + method.ToString() + "\".");
			}
			return client;
		}

		public static void Main (string[] args)
		{
			
			try {
				Console.WriteLine ("EE Midi Bot - Copyright (C) 2016 Andrew Story");
				Console.WriteLine ("This program comes with ABSOLUTELY NO WARRANTY. As this is free software, you");
				Console.WriteLine ("are welcome to redistribute it under the conditions of the GNU General Public");
				Console.WriteLine ("License (version 3).");
				Console.WriteLine ("See http://www.gnu.org/licenses/gpl.html for details.");


				Console.Write ("\n\nWelcome to the EE midi bot.\n\n" +
					"What login method would you like to use?\n" +
					"1) Simple\n" +
					"2) Facebook\n" +
					"3) Kongregate\n\n");
				Console.Write("Enter your choice: ");

				string response = Console.ReadLine ();
				int value = 0;
				value = Convert.ToInt32 (response);
				
				PlayerIOClient.Client client = DoLogin(value);

				Console.Write ("Room ID to join: ");
				string roomid = Console.ReadLine ();
				Console.WriteLine ("\n");

				PlayerIOClient.Connection con = client.Multiplayer.JoinRoom (roomid, new Dictionary<string, string> ());
				HandleConnection(con);

			} catch (PlayerIOError e) {
				System.Console.WriteLine (e.Message);
				return;
			}
			catch (Exception e){
				System.Console.WriteLine (e.Message);
				return;
			}
		}

		private static void HandleConnection(PlayerIOClient.Connection con){
			con.OnMessage += new MessageReceivedEventHandler (onMessage);
			con.Send ("init");
			con.Send ("init2");

			int dots = 0;
			while (WorldKey == "") {
				Console.Write ("Connecting");
				for (int i = 0; i < dots; ++i) {
					Console.Write (".");
				}
				Console.Write ("\r");
				System.Threading.Thread.Sleep (250);
				dots = (dots + 1) % 4;
			}
			Console.Write ("\n");
			Console.WriteLine ("Connected to room");

			Midi m = new Midi ();
			while (true) {
				Console.Write ("Command: ");
				string cmd = Console.ReadLine ();
				string[] cmds = cmd.Split (' ');
				switch (cmds [0]) {
					case "midi":
						m = new Midi ();
						if (!m.Read (cmd.Substring(5))) {
							Console.WriteLine ("Failed to load file. Reason:");
							Console.WriteLine (m.LastError);
						}
						Console.WriteLine ("Successfully loaded.");
					break;
					case "write":
						Console.WriteLine ("Writing to world (This will take a while...)");
						m.Rewind ();
						Write (con, m);
						Console.WriteLine ("Done!");
						break;
					case "quit": case "exit": case "close":
						throw new Exception ("Exiting...");
					case "help":
						Console.WriteLine("Available commands are:\n midi [path to midi file] - Load a midi file\n write - Save the midi file to the level\n help - Display this message.\n");
						break;
					default:
						Console.WriteLine("Unknown command. Try `help`.");
						break;
				}
			}
		}
	}
}
