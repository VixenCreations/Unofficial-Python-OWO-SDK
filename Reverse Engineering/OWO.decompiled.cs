using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Tasks;
using OWOGame.Controller;
using OWOGame.Infraestructure;

[assembly: CompilationRelaxations(8)]
[assembly: RuntimeCompatibility(WrapNonExceptionThrows = true)]
[assembly: Debuggable(DebuggableAttribute.DebuggingModes.Default | DebuggableAttribute.DebuggingModes.DisableOptimizations | DebuggableAttribute.DebuggingModes.IgnoreSymbolStoreSequencePoints | DebuggableAttribute.DebuggingModes.EnableEditAndContinue)]
[assembly: InternalsVisibleTo("Tests")]
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
[assembly: AssemblyCompany("OWO")]
[assembly: AssemblyConfiguration("Debug")]
[assembly: AssemblyDescription("OWO SDK for CSharp projects")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0+0e45093aee9899b77a718013fa020523d331f5b1")]
[assembly: AssemblyProduct("OWO")]
[assembly: AssemblyTitle("OWO")]
[assembly: AssemblyVersion("1.0.0.0")]
namespace OWOGame
{
	public class OWO
	{
		private static OWO instance;

		private readonly Client client;

		private readonly SendSensation send;

		private readonly StopSensation stop;

		private readonly Disconnect disconnect;

		private readonly Connect connect;

		private readonly RealTimeClock clock;

		private static OWO Instance => instance ?? (instance = new OWO(ClientFactory.Create(new UDPNetwork()), new RealTimeClock()));

		public static ConnectionState ConnectionState => Instance.client.State;

		/// <summary>
		/// Returns all the discovered OWO apps.
		/// </summary>
		public static string[] DiscoveredApps => Instance.client.DiscoveredServers.ToArray();

		internal OWO(Client client, RealTimeClock clock)
		{
			this.client = client;
			send = new SendSensation(client);
			stop = new StopSensation(client);
			connect = new Connect(client);
			disconnect = new Disconnect(client);
			this.clock = clock;
		}

		/// <summary>
		/// Assigns a GameAuth file that will be used to authenticate with the owo app.
		/// </summary>
		/// <param name="game"></param>
		public static void Configure(GameAuth game)
		{
			Instance.ConfigureB(game);
		}

		internal void ConfigureB(GameAuth game)
		{
			connect.Configure(game);
			send.Configure(game);
			stop.Configure(game);
		}

		/// <summary>
		/// Searches nearby owo apps to connect.
		/// </summary>
		/// <returns></returns>
		public static Task AutoConnect()
		{
			return Instance.AutoConnectB();
		}

		internal Task AutoConnectB()
		{
			return Task.Run((Func<Task>)connect.AutoConnect);
		}

		/// <summary>
		/// Searches for nearby OWO apps and stores them in the DiscoveredApps property.
		/// </summary>
		/// <returns></returns>
		public static void StartScan()
		{
			Instance.StartScanB();
		}

		internal void StartScanB()
		{
			Task.Run((Func<Task>)connect.ScanServer);
		}

		/// <summary>
		/// Connects to a list of specific owo apps.
		/// </summary>
		/// <returns></returns>
		public static Task Connect(params string[] ips)
		{
			return Instance.ConnectB(ips);
		}

		internal Task ConnectB(params string[] ips)
		{
			return Task.Run(() => connect.ManualConnect(ips));
		}

		/// <summary>
		/// Stops the current sensation.
		/// </summary>
		/// <returns></returns>
		public static void Stop()
		{
			Instance.StopB();
		}

		internal void StopB()
		{
			stop.Execute();
			send.ResetPriority();
		}

		/// <summary>
		/// Sends a sensation to the connected owo app.
		/// </summary>
		/// <param name="sensation"></param>
		/// <param name="muscles"></param>
		public static void Send(Sensation sensation, params Muscle[] muscles)
		{
			Instance.SendB(sensation, muscles);
		}

		internal void SendB(Sensation sensation, params Muscle[] muscles)
		{
			send.Execute(sensation.WithMuscles(muscles), clock.TotalMilliseconds);
		}

		/// <summary>
		/// Disconnects from the connected owo app.
		/// </summary>
		public static void Disconnect()
		{
			Instance.DisconnectB();
		}

		internal void DisconnectB()
		{
			disconnect.Execute();
		}
	}
	public class BakedSensation : Sensation
	{
		public readonly int id;

		public readonly string name;

		public readonly Family Family;

		public readonly Sensation reference;

		public readonly Icon icon;

		public override float Duration => reference.Duration;

		internal BakedSensation(int id, string name, Sensation reference, Icon icon, Family family)
		{
			this.id = id;
			this.name = name;
			this.reference = reference;
			this.icon = icon;
			Family = family;
		}

		public new static BakedSensation Parse(string message)
		{
			return ((Sensation)message) as BakedSensation;
		}

		public override Sensation MultiplyIntensityBy(Multiplier howMuch)
		{
			return new BakedSensation(id, name, reference, icon, Family);
		}

		public BakedSensation WithIcon(Icon icon)
		{
			return new BakedSensation(id, name, reference, icon, Family).WithPriority(base.Priority) as BakedSensation;
		}

		public BakedSensation BelongsTo(Family family)
		{
			return new BakedSensation(id, name, reference, icon, family).WithPriority(base.Priority) as BakedSensation;
		}

		public string Stringify()
		{
			return BakedSensationsBuilder.Stringify(this);
		}
	}
	internal static class BakedSensationsBuilder
	{
		private const string SEPARATOR = "~";

		public static string From(BakedSensation sensation)
		{
			return sensation.id.ToString();
		}

		public static string Stringify(BakedSensation sensation)
		{
			return string.Concat(sensation.id.ToString(), "~", sensation.name, "~", sensation.reference, "~", sensation.icon, "~", sensation.Family);
		}
	}
	internal static class GamesBuilder
	{
		private const string SEPARATOR = "#";

		public static string Build(GameAuth theGame)
		{
			if (theGame.sensations.Length == 0)
			{
				return string.Empty;
			}
			string text = theGame.sensations[0].Stringify();
			for (int i = 1; i < theGame.sensations.Length; i++)
			{
				text = text + "#\n" + theGame.sensations[i].Stringify();
			}
			return text;
		}
	}
	internal static class MusclesBuilder
	{
		public static string From(params Muscle[] muscles)
		{
			string text = From(muscles[0]);
			for (int i = 1; i < muscles.Length; i++)
			{
				text = text + "," + From(muscles[i]);
			}
			return text;
		}

		private static string From(Muscle muscle)
		{
			return $"{muscle.id}%{muscle.intensity}";
		}
	}
	internal static class SensationsBuilder
	{
		public static string From(Sensation sensation)
		{
			if (sensation is MicroSensation microsensation)
			{
				return From(microsensation);
			}
			if (sensation is SensationWithMuscles sensation2)
			{
				return From(sensation2);
			}
			if (sensation is SensationsSequence sequence)
			{
				return From(sequence);
			}
			return BakedSensationsBuilder.From(sensation as BakedSensation);
		}

		private static string From(SensationsSequence sequence)
		{
			string text = From(sequence.sensations[0]);
			for (int i = 1; i < sequence.sensations.Count; i++)
			{
				text = text + "&" + From(sequence.sensations[i]);
			}
			return text;
		}

		private static string From(SensationWithMuscles sensation)
		{
			return From(sensation.reference) + "|" + sensation.muscles.Stringify();
		}

		private static string From(MicroSensation microsensation)
		{
			return $"{microsensation.frequency},{(int)Math.Round(microsensation.duration * 10f)},{microsensation.intensity}," + $"{(int)Math.Round(microsensation.rampUp * 1000f)},{(int)Math.Round(microsensation.rampDown * 1000f)},{(int)Math.Round(microsensation.exitDelay * 10f)},{microsensation.name}";
		}
	}
	public static class Conversions
	{
		public static Multiplier ToPercentage(this float howMuch)
		{
			return (int)(howMuch * 100f);
		}
	}
	public static class MusclesExtensions
	{
		public static Muscle[] WithIntensity(this Muscle[] muscles, int intensity)
		{
			return muscles.Select((Muscle m) => m.WithIntensity(intensity)).ToArray();
		}

		public static Muscle Mirror(this Muscle of)
		{
			return new Muscle(MirrorOf(of.id), of.intensity);
		}

		public static Muscle MultiplyIntensityBy(this Muscle of, Multiplier howMuch)
		{
			return new Muscle(of.id, howMuch * of.intensity);
		}

		public static Muscle[] MultiplyIntensityBy(this Muscle[] of, Multiplier howMuch)
		{
			return of.Select((Muscle muscle) => muscle.MultiplyIntensityBy(howMuch)).ToArray();
		}

		public static Muscle[] Mirror(this Muscle[] of)
		{
			return of.Select(Mirror).ToArray();
		}

		private static int MirrorOf(int aPosition)
		{
			return (aPosition % 2 == 0) ? (aPosition + 1) : (aPosition - 1);
		}

		public static string Stringify(this Muscle muscle)
		{
			return MusclesBuilder.From(muscle);
		}

		public static string Stringify(this Muscle[] muscles)
		{
			return MusclesBuilder.From(muscles);
		}
	}
	public static class SensationExtensions
	{
		public static Sensation WithPriority(this Sensation source, int priority)
		{
			source.Priority = priority;
			return source;
		}

		public static Sensation Append(this Sensation source, Sensation addend)
		{
			return new SensationsSequence(source, addend).WithPriority(source.Priority);
		}

		public static BakedSensation Bake(this Sensation source, int id, string name)
		{
			if (source is BakedSensation result)
			{
				return result;
			}
			return new BakedSensation(id, name, source, Icon.Empty, Family.None).WithPriority(source.Priority) as BakedSensation;
		}

		public static Sensation WithMuscles(this Sensation source, params Muscle[] muscles)
		{
			if (muscles.Length == 0 || source is SensationWithMuscles)
			{
				return source;
			}
			if (source is SensationsSequence sensationsSequence)
			{
				return new SensationsSequence(sensationsSequence.sensations.Select((Sensation s) => s.WithMuscles(muscles)).ToArray()).WithPriority(source.Priority);
			}
			return new SensationWithMuscles(source, muscles).WithPriority(source.Priority);
		}
	}
	public struct Family
	{
		private readonly string name;

		public static Family None { get; } = "";


		private Family(string name)
		{
			this.name = name;
		}

		public static implicit operator string(Family family)
		{
			return family.name;
		}

		public static implicit operator Family(string name)
		{
			return new Family(name);
		}

		public override string ToString()
		{
			return name;
		}
	}
	public class GameAuth
	{
		public readonly string id;

		public readonly BakedSensation[] sensations = new BakedSensation[0];

		public static GameAuth Empty => Create().WithId("0");

		internal GameAuth()
		{
		}

		internal GameAuth(string id, params BakedSensation[] sensations)
		{
			this.id = id;
			this.sensations = sensations;
		}

		public GameAuth WithId(string id)
		{
			return new GameAuth(id, sensations);
		}

		public static GameAuth Parse(string auth)
		{
			return auth;
		}

		public static GameAuth Create(params BakedSensation[] sensations)
		{
			return new GameAuth("0", sensations);
		}

		public override string ToString()
		{
			return this;
		}

		public static implicit operator GameAuth(string auth)
		{
			return GamesParser.From(auth);
		}

		public static implicit operator string(GameAuth auth)
		{
			return GamesBuilder.Build(auth);
		}
	}
	public struct Icon
	{
		[StructLayout(LayoutKind.Sequential, Size = 1)]
		public struct Impact
		{
			private const string PREFIX = "Impact-";

			public static Icon Ball => new Icon("Impact-" + 0);

			public static Icon Dart => new Icon("Impact-" + 1);

			public static Icon Punch => new Icon("Impact-" + 2);

			public static Icon Bullet => new Icon("Impact-" + 3);
		}

		[StructLayout(LayoutKind.Sequential, Size = 1)]
		public struct Weapon
		{
			private const string PREFIX = "Weapon-";

			public static Icon Axe => new Icon("Weapon-" + 0);

			public static Icon Dagger => new Icon("Weapon-" + 1);

			public static Icon Gun => new Icon("Weapon-" + 2);

			public static Icon SubMachineGun => new Icon("Weapon-" + 3);
		}

		private readonly string name;

		public static Icon Empty => new Icon("0");

		public static Icon Death => new Icon("Death-0");

		public static Icon Spiders => new Icon("Spider-0");

		public static Icon Weight => new Icon("Weight-0");

		public static Icon Environment => new Icon("Environment-0");

		public static Icon Alert => new Icon("Alert-0");

		public static Icon Victory => new Icon("Victory-0");

		internal Icon(string name)
		{
			this.name = name;
		}

		public static implicit operator Icon(string message)
		{
			return new Icon(message);
		}

		public static implicit operator string(Icon icon)
		{
			return icon.name;
		}

		public override string ToString()
		{
			return this;
		}
	}
	internal static class Math
	{
		public static T Clamp<T>(T val, T min, T max) where T : IComparable<T>
		{
			if (val.CompareTo(min) < 0)
			{
				return min;
			}
			if (val.CompareTo(max) > 0)
			{
				return max;
			}
			return val;
		}

		public static float Round(float value, int decimals = 1)
		{
			return (float)System.Math.Round(value, decimals);
		}
	}
	public class MicroSensation : Sensation
	{
		public readonly int frequency;

		public readonly float duration;

		public readonly int intensity;

		public readonly float rampUp;

		public readonly float rampDown;

		public readonly float exitDelay;

		public readonly string name;

		public override float Duration => duration + exitDelay;

		internal MicroSensation(int frequency, float duration, int intensity, float rampUp, float rampDown, float exitDelay, string name = "")
		{
			this.frequency = Math.Clamp(frequency, 1, 100);
			this.duration = Math.Round(Math.Clamp(duration, 0.1f, 20f));
			this.intensity = Math.Clamp(intensity, 0, 100);
			this.rampUp = Math.Round(Math.Clamp(rampUp, 0f, 2f));
			this.rampDown = Math.Round(Math.Clamp(rampDown, 0f, 2f));
			this.exitDelay = Math.Round(Math.Clamp(exitDelay, 0f, 20f));
			this.name = name;
		}

		public override Sensation MultiplyIntensityBy(Multiplier howMuch)
		{
			return new MicroSensation(frequency, duration, intensity * howMuch, rampUp, rampDown, exitDelay, name);
		}

		public MicroSensation WithName(string name)
		{
			return new MicroSensation(frequency, duration, intensity, rampUp, rampDown, exitDelay, name).WithPriority(base.Priority) as MicroSensation;
		}
	}
	public struct Multiplier
	{
		public readonly int value;

		private Multiplier(int value)
		{
			this.value = Math.Clamp(value, 0, value);
		}

		public static Multiplier operator *(Multiplier theFirst, int theSecond)
		{
			return new Multiplier(theFirst.value * theSecond / 100);
		}

		public static implicit operator Multiplier(int howmuch)
		{
			return new Multiplier(howmuch);
		}

		public static implicit operator int(Multiplier howmuch)
		{
			return howmuch.value;
		}
	}
	public readonly struct Muscle
	{
		public readonly int id;

		public readonly int intensity;

		public static Muscle Pectoral_R = new Muscle(0);

		public static Muscle Pectoral_L = new Muscle(1);

		public static Muscle Abdominal_R = new Muscle(2);

		public static Muscle Abdominal_L = new Muscle(3);

		public static Muscle Arm_R = new Muscle(4);

		public static Muscle Arm_L = new Muscle(5);

		public static Muscle Dorsal_R = new Muscle(6);

		public static Muscle Dorsal_L = new Muscle(7);

		public static Muscle Lumbar_R = new Muscle(8);

		public static Muscle Lumbar_L = new Muscle(9);

		public static Muscle[] All => Front.Concat(Back).ToArray();

		public static Muscle[] Front => new Muscle[6] { Pectoral_R, Pectoral_L, Abdominal_R, Abdominal_L, Arm_R, Arm_L };

		public static Muscle[] Back => new Muscle[4] { Dorsal_R, Dorsal_L, Lumbar_R, Lumbar_L };

		internal Muscle(int id, int intensity = 100)
		{
			this.id = id;
			this.intensity = Math.Clamp(intensity, 0, 100);
		}

		public Muscle WithIntensity(int intensity)
		{
			return new Muscle(id, intensity);
		}

		public static Muscle[] Parse(string muscles)
		{
			return MusclesParser.Parse(muscles);
		}
	}
	internal static class BakedSensationsParser
	{
		private const char SEPARATOR = '~';

		public static bool CanParse(string message)
		{
			return (!message.Contains(',') && !message.Contains('|')) || message.Contains('~');
		}

		public static BakedSensation From(string message)
		{
			if (!message.Contains('~'))
			{
				return SensationsFactory.Create().Bake(int.Parse(message), "");
			}
			string[] array = message.Split('~');
			return new BakedSensation(int.Parse(array[0]), array[1], array[2], array[3], FamilyFrom(array));
		}

		private static Family FamilyFrom(string[] parameters)
		{
			return (parameters.Length < 5) ? Family.None : ((Family)parameters[4]);
		}
	}
	internal static class GamesParser
	{
		public static GameAuth From(string auth)
		{
			string[] array = auth.Split('#');
			if (int.TryParse(auth, out var _))
			{
				return new GameAuth().WithId(auth);
			}
			if (string.IsNullOrEmpty(array[0]))
			{
				return new GameAuth();
			}
			return new GameAuth("0", array.Select((string s) => BakedSensationsParser.From(s)).ToArray());
		}
	}
	internal static class MicrosensationsParser
	{
		public const char SEPARATOR = ',';

		public static MicroSensation From(string message)
		{
			string[] array = message.Split(',');
			return new MicroSensation(int.Parse(array[0]), float.Parse(array[1]) / 10f, int.Parse(array[2]), float.Parse(array[3]) / 1000f, float.Parse(array[4]) / 1000f, float.Parse(array[5]) / 10f, NameFrom(array));
		}

		private static string NameFrom(string[] parameters)
		{
			return (parameters.Length >= 7) ? parameters[6] : "";
		}
	}
	internal static class MusclesParser
	{
		public static Muscle[] Parse(string message)
		{
			string[] source = message.Split(',');
			return source.Select((string m) => ParseSingle(m)).ToArray();
		}

		public static Muscle ParseSingle(string message)
		{
			string[] array = message.Split('%');
			return new Muscle(int.Parse(array[0]), int.Parse(array[1]));
		}
	}
	internal static class SensationsParser
	{
		public static Sensation From(string message)
		{
			if (BakedSensationsParser.CanParse(message))
			{
				return BakedSensationsParser.From(message);
			}
			if (SequenceParser.CanParse(message))
			{
				return SequenceParser.From(message);
			}
			if (SensationWithMusclesParser.CanParse(message))
			{
				return SensationWithMusclesParser.From(message);
			}
			return MicrosensationsParser.From(message);
		}
	}
	internal static class SensationWithMusclesParser
	{
		public const char SEPARATOR = '|';

		public static bool CanParse(string message)
		{
			return message.Contains('|');
		}

		public static SensationWithMuscles From(string message)
		{
			string[] array = message.Split('|');
			return new SensationWithMuscles(array[0], MusclesParser.Parse(array[1]));
		}
	}
	internal static class SequenceParser
	{
		public const char SEPARATOR = '&';

		public static bool CanParse(string message)
		{
			return message.Contains('&');
		}

		public static Sensation From(string message)
		{
			string[] source = message.Split('&');
			Sensation[] sensations = ((IEnumerable<string>)source).Select((Func<string, Sensation>)((string s) => s)).ToArray();
			return new SensationsSequence(sensations);
		}
	}
	public abstract class Sensation
	{
		public int Priority { get; set; } = 0;


		public abstract float Duration { get; }

		/// <summary>
		/// SensationsFactory.Create(100, .1f)
		/// </summary>
		public static Sensation Ball => SensationsFactory.Create();

		/// <summary>
		/// SensationsFactory.Create(10, .1f)
		/// </summary>
		public static Sensation Dart => SensationsFactory.Create(10);

		/// <summary>
		/// Sensation.DaggerEntry.Append(Sensation.DaggerMovement)
		/// </summary>
		public static Sensation Dagger => DaggerEntry.Append(DaggerMovement);

		/// <summary>
		/// SensationsFactory.Create(60, .2f)
		/// </summary>
		public static Sensation DaggerEntry => SensationsFactory.Create(60, 0.2f);

		/// <summary>
		/// SensationsFactory.Create(100, 2, 100, .3f, .1f)
		/// </summary>
		public static Sensation DaggerMovement => SensationsFactory.Create(100, 2f, 100, 0.3f, 0.1f);

		/// <summary>
		/// Sensation.ShotEntry.Append(Sensation.ShotExit).Append(Sensation.ShotBleeding);
		/// </summary>
		public static Sensation ShotWithExit => ShotEntry.Append(ShotExit).Append(ShotBleeding);

		/// <summary>
		/// SensationsFactory.Create(30, .1f).WithMuscles(Muscle.Pectoral_R)
		/// </summary>
		public static Sensation ShotEntry => SensationsFactory.Create(30).WithMuscles(Muscle.Pectoral_R);

		/// <summary>
		/// SensationsFactory.Create(20, .1f).WithMuscles(Muscle.Dorsal_R)
		/// </summary>
		public static Sensation ShotExit => SensationsFactory.Create(20).WithMuscles(Muscle.Dorsal_R);

		/// <summary>
		/// SensationsFactory.Create(50, .5f, 80, 0, .3f).WithMuscles(Muscle.Pectoral_R, Muscle.Pectoral_L)
		/// </summary>
		public static Sensation ShotBleeding => SensationsFactory.Create(50, 0.5f, 80, 0f, 0.3f).WithMuscles(Muscle.Pectoral_R, Muscle.Pectoral_L);

		public static Sensation Parse(string message)
		{
			return message;
		}

		public static implicit operator Sensation(string message)
		{
			return SensationsParser.From(message);
		}

		public static implicit operator string(Sensation sensation)
		{
			return SensationsBuilder.From(sensation);
		}

		public override string ToString()
		{
			return this;
		}

		public abstract Sensation MultiplyIntensityBy(Multiplier howMuch);
	}
	public static class SensationsFactory
	{
		public static MicroSensation Create(int frequency = 100, float durationSeconds = 0.1f, int intensityPercentage = 100, float rampUpMillis = 0f, float rampDownMillis = 0f, float exitDelaySeconds = 0f)
		{
			return new MicroSensation(frequency, durationSeconds, intensityPercentage, rampUpMillis, rampDownMillis, exitDelaySeconds);
		}
	}
	public class SensationsSequence : Sensation
	{
		public readonly List<Sensation> sensations;

		public override float Duration => sensations.Sum((Sensation s) => s.Duration);

		public SensationsSequence(params Sensation[] sensations)
		{
			this.sensations = new List<Sensation>(sensations);
		}

		public override Sensation MultiplyIntensityBy(Multiplier howMuch)
		{
			return new SensationsSequence(sensations.Select((Sensation s) => s.MultiplyIntensityBy(howMuch)).ToArray());
		}
	}
	public class SensationWithMuscles : Sensation
	{
		public readonly Sensation reference;

		public readonly Muscle[] muscles;

		public override float Duration => reference.Duration;

		public SensationWithMuscles(Sensation reference, Muscle[] muscles)
		{
			this.reference = reference;
			this.muscles = muscles;
		}

		public override Sensation MultiplyIntensityBy(Multiplier howMuch)
		{
			return new SensationWithMuscles(reference, muscles.MultiplyIntensityBy(howMuch));
		}
	}
	public enum ConnectionState
	{
		Connected,
		Disconnected,
		Connecting
	}
	public interface Network
	{
		List<Address> ConnectedServers { get; }

		ConnectionState State { get; set; }

		bool IsConnecting { get; }

		bool IsConnected { get; }

		void SendTo(string message, string addressee);

		string Listen(out Address sender);

		void Connect(Address server);

		void Disconnect();

		void Close();

		void PortTo(int newPort);
	}
	public class Client
	{
		private readonly Network network;

		private readonly SendMessage sendMessage;

		private readonly FindServer findServer;

		private readonly ListenForDisconnection disconnection;

		private readonly CandidatesVault candidates;

		public ConnectionState State => network.State;

		public bool IsConnected => network.ConnectedServers != null && network.ConnectedServers.Count != 0;

		private bool CanScan => network.State == ConnectionState.Disconnected;

		public List<string> DiscoveredServers => candidates.StoredServers;

		internal Client(Network network, SendMessage sendMessage, FindServer findServer, ListenForDisconnection disconnection, CandidatesVault keys)
		{
			this.network = network;
			this.sendMessage = sendMessage;
			this.findServer = findServer;
			this.disconnection = disconnection;
			candidates = keys;
		}

		~Client()
		{
			Close();
		}

		internal Task ScanServer()
		{
			if (!CanScan)
			{
				return Task.CompletedTask;
			}
			candidates.Clean();
			return findServer.Scan();
		}

		internal Task FindServer(string auth, params string[] addresses)
		{
			if (!CanScan)
			{
				return Task.CompletedTask;
			}
			if (addresses[0] == "255.255.255.255")
			{
				candidates.Clean();
			}
			if (addresses.Length == 1)
			{
				return FindServer(addresses[0], auth);
			}
			return Task.Run(() => findServer.Execute(addresses, auth));
		}

		private async Task FindServer(string address, string auth)
		{
			await findServer.ExecuteWithAbscense(address, auth);
			ListenForDisconnection(address, auth);
		}

		private async Task ListenForDisconnection(string addressee, string auth)
		{
			if (await disconnection.Listen())
			{
				Disconnect();
				await FindServer(addressee, auth);
			}
		}

		public void Send(string message)
		{
			network.ConnectedServers.ForEach(delegate(Address server)
			{
				sendMessage.Execute(message, server);
			});
		}

		public void Disconnect()
		{
			network.Disconnect();
		}

		public void Close()
		{
			network.Close();
		}

		public void ChangeConnectionAttemptRate(int newRate)
		{
			findServer.DelayTime = newRate;
			disconnection.delayTime = newRate;
		}

		public void PortTo(int newPort)
		{
			network.PortTo(newPort);
		}
	}
	internal static class ClientFactory
	{
		public static Client Create(Network network, Message secretKey = default(Message), CandidatesVault keysVault = null, int scanDelayMs = 500)
		{
			if (keysVault == null)
			{
				keysVault = new CandidatesVault();
			}
			if (secretKey.addressee != null)
			{
				keysVault.Store(secretKey);
			}
			SendMessage sendMessage = new SendMessage(network);
			NotifyAbscense notifyAbscense = new NotifyAbscense(network, keysVault);
			SendAuthMessage sendAuth = new SendAuthMessage(sendMessage, keysVault);
			ReceiveAvailableApp receiveSecretKey = new ReceiveAvailableApp(keysVault, network);
			FindServer findServer = new FindServer(network, notifyAbscense, receiveSecretKey, sendAuth, keysVault)
			{
				DelayTime = scanDelayMs
			};
			ListenForDisconnection disconnection = new ListenForDisconnection(network);
			return new Client(network, sendMessage, findServer, disconnection, keysVault);
		}
	}
	internal class FindServer
	{
		private readonly Network network;

		private readonly NotifyAbscense notifyAbscense;

		private readonly ReceiveAvailableApp interpretAppMessage;

		private readonly SendAuthMessage sendAuth;

		private readonly CandidatesVault candidates;

		public int DelayTime = 500;

		private Task TimeBetweenAttempts => Task.Delay(DelayTime);

		public FindServer(Network network, NotifyAbscense notifyAbscense, ReceiveAvailableApp receiveSecretKey, SendAuthMessage sendAuth, CandidatesVault keys)
		{
			this.network = network;
			this.notifyAbscense = notifyAbscense;
			interpretAppMessage = receiveSecretKey;
			this.sendAuth = sendAuth;
			candidates = keys;
		}

		public async Task ExecuteWithAbscense(Address addressee, string auth)
		{
			await Execute(new string[1] { addressee }, auth);
			notifyAbscense.Execute(auth);
		}

		public async Task Execute(string[] addressees, string auth)
		{
			network.State = ConnectionState.Connecting;
			foreach (string address2 in addressees)
			{
				if (candidates.ContainsCandidate(address2))
				{
					sendAuth.Execute(auth, address2);
				}
			}
			do
			{
				Address sender;
				string lastMessage = ReceiveMessage(out sender);
				if (string.IsNullOrEmpty(lastMessage))
				{
					foreach (string address in addressees)
					{
						NotifyPresence(address);
					}
				}
				if (lastMessage.Equals("okay"))
				{
					candidates.Store(new Message("", sender));
					sendAuth.Execute(auth, sender);
				}
				else if (IsConnectionVerification(addressees, sender, lastMessage))
				{
					network.Connect(sender);
				}
				await TimeBetweenAttempts;
				sender = default(Address);
			}
			while (network.ConnectedServers.Count != addressees.Count() && network.State != ConnectionState.Disconnected);
		}

		private bool IsConnectionVerification(string[] addresse, string sender, string lastMessage)
		{
			return lastMessage.Equals("pong") && (addresse.Contains(sender) || addresse[0] == "255.255.255.255");
		}

		private string ReceiveMessage(out Address sender)
		{
			return network.Listen(out sender);
		}

		public async Task Scan()
		{
			while (network.State == ConnectionState.Disconnected)
			{
				NotifyPresence("255.255.255.255");
				await interpretAppMessage.Execute();
				await TimeBetweenAttempts;
			}
		}

		private void NotifyPresence(string addressee)
		{
			network.SendTo("ping", addressee);
		}
	}
	internal class ListenForDisconnection
	{
		private readonly Network network;

		public int delayTime = 50;

		private Task ListenDelay => Task.Delay(delayTime);

		public ListenForDisconnection(Network network)
		{
			this.network = network;
		}

		public async Task<bool> Listen()
		{
			while (network.State == ConnectionState.Connected)
			{
				Address sender;
				string message = network.Listen(out sender);
				if (message.Equals("OWO_Close") && network.ConnectedServers.Contains(sender))
				{
					return true;
				}
				await ListenDelay;
				sender = default(Address);
			}
			return false;
		}
	}
	internal class NotifyAbscense
	{
		private readonly Network network;

		private readonly CandidatesVault candidates;

		public NotifyAbscense(Network network, CandidatesVault candidates)
		{
			this.network = network;
			this.candidates = candidates;
		}

		public void Execute(string authCommand)
		{
			string text = authCommand.Split('*')[0];
			foreach (string storedServer in candidates.StoredServers)
			{
				string message = text + "*GAMEUNAVAILABLE";
				network.SendTo(message, storedServer);
			}
		}
	}
	internal class ReceiveAvailableApp
	{
		private readonly Network network;

		private readonly CandidatesVault keys;

		public ReceiveAvailableApp(CandidatesVault secretKeys, Network network)
		{
			keys = secretKeys;
			this.network = network;
		}

		public Task Execute()
		{
			Address sender;
			string text = network.Listen(out sender);
			if (!text.Equals("okay"))
			{
				return Task.CompletedTask;
			}
			keys.Store(new Message("", sender));
			return Task.CompletedTask;
		}
	}
	internal class SendAuthMessage
	{
		private readonly SendMessage send;

		private readonly CandidatesVault keys;

		public SendAuthMessage(SendMessage send, CandidatesVault keys)
		{
			this.send = send;
			this.keys = keys;
		}

		public void Execute(string auth, Address addressee)
		{
			if (addressee.Equals(Address.Any))
			{
				foreach (string storedServer in keys.StoredServers)
				{
					send.Execute(auth, storedServer);
				}
				return;
			}
			if (keys.ContainsCandidate(addressee))
			{
				send.Execute(auth, addressee);
			}
		}
	}
	internal class SendMessage
	{
		private readonly Network network;

		public SendMessage(Network network)
		{
			this.network = network;
		}

		public void Execute(string message, Address addressee)
		{
			if (addressee.IsValid)
			{
				network.SendTo(message, addressee);
			}
		}
	}
	internal class ASCIIEncoder
	{
		public string Decode(byte[] buffer, int messageLength)
		{
			return Encoding.ASCII.GetString(buffer, 0, messageLength);
		}

		public byte[] Encode(string message)
		{
			return Encoding.ASCII.GetBytes(message);
		}
	}
	internal class UDPNetwork : Network
	{
		private readonly byte[] buffer;

		private readonly Socket socket;

		private readonly ASCIIEncoder encoding;

		private int PORT = 54020;

		public List<Address> ConnectedServers { get; private set; } = new List<Address>();


		public ConnectionState State { get; set; } = ConnectionState.Disconnected;


		public bool IsConnecting => State == ConnectionState.Connecting;

		public bool IsConnected => State == ConnectionState.Connected;

		public UDPNetwork()
		{
			buffer = new byte[1024];
			socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
			socket.EnableBroadcast = true;
			socket.ReceiveTimeout = 2500;
			socket.Blocking = false;
			encoding = new ASCIIEncoder();
		}

		public string Listen(out Address address)
		{
			try
			{
				EndPoint remoteEP = new IPEndPoint(0L, 0);
				string result = encoding.Decode(buffer, socket.ReceiveFrom(buffer, ref remoteEP));
				address = new Address((remoteEP as IPEndPoint).Address.ToString());
				return result;
			}
			catch
			{
				address = Address.Empty;
				return string.Empty;
			}
		}

		public void SendTo(string message, string addressee)
		{
			socket.SendTo(encoding.Encode(message), new IPEndPoint(IPAddress.Parse(addressee), PORT));
		}

		public void Connect(Address address)
		{
			if (!ConnectedServers.Contains(address))
			{
				ConnectedServers.Add(address);
			}
			State = ConnectionState.Connected;
		}

		public void Disconnect()
		{
			ConnectedServers.Clear();
			State = ConnectionState.Disconnected;
		}

		public void Close()
		{
			socket.Close();
		}

		public void PortTo(int newPort)
		{
			PORT = newPort;
		}
	}
	public struct Address
	{
		public readonly string value;

		public bool IsValid => !string.IsNullOrEmpty(value);

		public static Address Any => new Address("255.255.255.255");

		public static Address Empty => new Address(string.Empty);

		public static Address Null => new Address(null);

		public Address(string value)
		{
			this.value = value;
		}

		public static implicit operator string(Address addressee)
		{
			return addressee.value;
		}

		public static implicit operator Address(string value)
		{
			return new Address(value);
		}

		public static Address Create(string ip)
		{
			return new Address(ip);
		}
	}
	internal class CandidatesVault
	{
		private HashSet<Address> candidateServers = new HashSet<Address>();

		public Address LastApp => candidateServers.FirstOrDefault();

		public List<string> StoredServers => candidateServers.Select((Address candidate) => candidate.value).ToList();

		public void Store(Message message)
		{
			if (message.HasAddressee)
			{
				candidateServers.Add(message.addressee);
			}
		}

		public void Clean()
		{
			candidateServers.Clear();
		}

		public bool ContainsCandidate(Address address)
		{
			return candidateServers.Contains(address);
		}
	}
	internal struct Message
	{
		public readonly string value;

		public readonly string addressee;

		public bool IsEmpty => string.IsNullOrEmpty(value);

		public bool HasAddressee => !string.IsNullOrEmpty(addressee);

		public static Message Invalid => new Message(string.Empty, Address.Empty);

		public Message(string value, string addresseeIP)
		{
			this.value = value;
			addressee = addresseeIP;
		}
	}
}
namespace OWOGame.Infraestructure
{
	public class RealTimeClock
	{
		private readonly Stopwatch stopwatch;

		public long TotalMilliseconds => (long)stopwatch.Elapsed.TotalMilliseconds;

		public RealTimeClock()
		{
			stopwatch = new Stopwatch();
			stopwatch.Start();
		}
	}
}
namespace OWOGame.Controller
{
	internal class Connect
	{
		private GameAuth game = GameAuth.Empty;

		private readonly Client client;

		public Connect(Client client)
		{
			this.client = client;
		}

		public Task ScanServer()
		{
			return client.ScanServer();
		}

		public Task AutoConnect()
		{
			return client.FindServer($"{game.id}*AUTH*{game}", "255.255.255.255");
		}

		public Task ManualConnect(params string[] ips)
		{
			return client.FindServer($"{game.id}*AUTH*{game}", ips);
		}

		public void Configure(GameAuth game)
		{
			this.game = game;
		}
	}
	internal class Disconnect
	{
		private readonly Client client;

		public Disconnect(Client client)
		{
			this.client = client;
		}

		public void Execute()
		{
			if (client.State != ConnectionState.Disconnected)
			{
				client.Disconnect();
			}
		}
	}
	internal class SendMessage
	{
		private readonly Client client;

		public SendMessage(Client client)
		{
			this.client = client;
		}

		public void Execute(string message)
		{
			if (client.IsConnected)
			{
				client.Send(message);
			}
		}
	}
	internal class SendSensation : SendMessage
	{
		private long whenLastSensationEnds;

		private int lastPriority = -1;

		private GameAuth game = GameAuth.Empty;

		public SendSensation(Client network)
			: base(network)
		{
		}

		public void Execute(Sensation sensation, long currentTimeMs)
		{
			if (lastPriority <= sensation.Priority || currentTimeMs >= whenLastSensationEnds)
			{
				Execute($"{game.id}*SENSATION*{sensation}");
				whenLastSensationEnds = currentTimeMs + (int)(sensation.Duration * 1000f);
				lastPriority = sensation.Priority;
			}
		}

		public void Configure(GameAuth game)
		{
			this.game = game;
		}

		public void ResetPriority()
		{
			whenLastSensationEnds = 0L;
		}
	}
	internal class StopSensation : SendMessage
	{
		private GameAuth game = GameAuth.Empty;

		public StopSensation(Client network)
			: base(network)
		{
		}

		public void Execute()
		{
			Execute(game.id + "*STOP");
		}

		public void Configure(GameAuth game)
		{
			this.game = game;
		}
	}
}
