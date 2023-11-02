using System;
using System.Buffers;
using System.Net;
using System.Text;
using System.Threading.Channels;
using SuperSocket.Channel;
using SuperSocket.Client;
using SuperSocket.ProtoBase;

namespace OpenVPNManagementClient
{
	internal class Program
	{
		static AutoResetEvent _packageEvent;
		static OpenVPNManagementPackage _package;
		static SemaphoreSlim _semaphoreSlim = new SemaphoreSlim(1, 1);
		static IEasyClient<OpenVPNManagementPackage> _client;
		//static IEasyClient<TextPackageInfo> _client;
		static bool _isOpen = false;
		static StringBuilder log = new StringBuilder();

		static async Task Main(string[] args)
		{
			await Connect();
			await Task.Delay(2000); // Reason is to make sure the server responds with the greeting
			await _client.SendAsync(Encoding.ASCII.GetBytes("status" + "\r\n"));

			Console.WriteLine("Press ESC to stop");
			do
			{
				while (!Console.KeyAvailable)
				{
					// Do something
				}
			} while (Console.ReadKey(true).Key != ConsoleKey.Escape);
		}

		static async Task Connect()
		{
			var packageEvent = new AutoResetEvent(false);

			// Try this
			//_client = new EasyClient<TextPackageInfo>(new CustomFilter(), new SuperSocket.Channel.ChannelOptions()).AsClient();

			// vs.
			_client = new EasyClient<OpenVPNManagementPackage>(new LinePipelineFilterOpenVPN(), new SuperSocket.Channel.ChannelOptions()).AsClient();



			_client.PackageHandler += Client_PackageHandler;
			_client.Closed += Client_Closed;
			_isOpen = await _client.ConnectAsync(new IPEndPoint(IPAddress.Parse("83.68.236.206"), 7075));

			if (_isOpen == true)
			{
				_client.StartReceive();
			}
			else
			{
				Console.WriteLine("Trying to reconnect");
				await Task.Delay(5000);
				await Connect();
			}
		}

		static async void Client_Closed(object? sender, EventArgs e)
		{
			
		}

		static async ValueTask Client_PackageHandler(EasyClient<OpenVPNManagementPackage> sender, OpenVPNManagementPackage package)
		{
			Console.WriteLine($"Client_PackageHandler: {package?.Text}");
			//if (_packageEvent != null && package.PackageType != OpenVPNManagementPackageType.EVENTDATA)
			//{
			//	_package = package;
			//	_packageEvent.Set();
			//	return;
			//} else if (package.PackageType == OpenVPNManagementPackageType.EVENTDATA)
			//{
			//	//lock (log)
			//	//{
			//	//	if (log.Length > 1000)
			//	//		log.Clear();

			//		log.AppendLine(package.Text);

			//		_logger.LogDebug($"{package.Text}");
			//	//}
			//}

			await Task.CompletedTask;
		}

		async Task<OpenVPNManagementPackage> SendCommand(string command)
		{
			if (_isOpen == false)
				throw new Exception("The OpenVPN management connection is currentley closed");

			await _semaphoreSlim.WaitAsync();
			OpenVPNManagementPackage package = null;
			try
			{
				_packageEvent = new AutoResetEvent(false);
				await _client.SendAsync(Encoding.ASCII.GetBytes(command + "\r\n"));

				// Code for supporting the case that a caller expects a response 
				//_packageEvent.WaitOne(20000);
				//_logger.LogDebug(_package.Text);
				//package = (OpenVPNManagementPackage)_package.Clone();
				//return response;
			}
			finally
			{
				_packageEvent = null;
				_package = null;
				_semaphoreSlim.Release();
			}

			return package;
		}

		async Task Disconnect()
		{
			Console.WriteLine("Disconnected");
			_client.Closed -= Client_Closed;
			_client.PackageHandler -= Client_PackageHandler;
			await _client.CloseAsync();
		}
	}



	public class OpenVPNManagementPackage : ICloneable
	{
		public OpenVPNManagementPackageType PackageType { get; set; }
		public string Text { get; set; }

		public object Clone()
		{
			return MemberwiseClone();
		}
	}

	public enum OpenVPNManagementPackageType
	{
		SUCCESS = 0,
		ERROR = 1,
		EVENTDATA = 2,
		RESPONSE = 3
	}

	public class LinePipelineFilterOpenVPN : TerminatorPipelineFilter<OpenVPNManagementPackage>
	{
		protected Encoding Encoding { get; private set; }

		protected StringBuilder Data { get; private set; }

		public LinePipelineFilterOpenVPN()
			: this(Encoding.ASCII)
		{

		}

		public LinePipelineFilterOpenVPN(Encoding encoding)
			: base(new[] { (byte)'\r', (byte)'\n' })
		{
			Encoding = encoding;
			Data = new StringBuilder();
		}

		protected override OpenVPNManagementPackage DecodePackage(ref ReadOnlySequence<byte> buffer)
		{
			var text = buffer.GetString(Encoding);
			OpenVPNManagementPackageType packageType = OpenVPNManagementPackageType.RESPONSE;

			if (text.StartsWith("ERROR:") == true)
			{
				packageType = OpenVPNManagementPackageType.ERROR;
				// Done
				return new OpenVPNManagementPackage { Text = text, PackageType = OpenVPNManagementPackageType.ERROR };
			}
			else if (text.StartsWith("SUCCESS:") == true)
			{
				packageType = OpenVPNManagementPackageType.SUCCESS;
				return new OpenVPNManagementPackage { Text = text, PackageType = OpenVPNManagementPackageType.SUCCESS };
			}
			else if (text.StartsWith(">") == true)
			{
				packageType = OpenVPNManagementPackageType.EVENTDATA;
				// Log
				return new OpenVPNManagementPackage { Text = text, PackageType = OpenVPNManagementPackageType.EVENTDATA };
			}
			else if (text.Contains("END") == true)
			{
				packageType = OpenVPNManagementPackageType.RESPONSE;
				Console.WriteLine("CONTAINS END");
				text = Data.ToString();
				Data = new StringBuilder();
			}
			else
			{
				Data.AppendLine(text);
				Console.WriteLine(Data.ToString());
				return null;
			}


			return new OpenVPNManagementPackage { Text = text, PackageType = packageType };

		}
	}

	public class CustomFilter : PipelineFilterBase<TextPackageInfo>
	{
		private StringBuilder _result = new StringBuilder();

		public CustomFilter()
		{
		}

		public override TextPackageInfo Filter(ref SequenceReader<byte> reader)
		{
			var terminator = new ReadOnlyMemory<byte>(new[] { (byte)'\r', (byte)'\n' });
			var terminatorSpan = terminator.Span;
			//Console.WriteLine("Sequence length:" + reader.Sequence.Length);
			//Console.WriteLine("Reader length:" + reader.Length);

			if (!reader.TryReadTo(out ReadOnlySequence<byte> pack, terminatorSpan, advancePastDelimiter: false))
				return null; // Not reached line end

			var contentInPack = pack.GetString(Encoding.ASCII);

			_result.AppendLine(contentInPack);
			Console.WriteLine(_result);

			reader.Advance(terminator.Length);


			//if (contentInPack.Contains("END") == false)
			//	return null;


			return DecodePackage(ref pack);
		}

		protected override TextPackageInfo DecodePackage(ref ReadOnlySequence<byte> buffer)
		{
			return new TextPackageInfo { Text = buffer.GetString(Encoding.UTF8) };
		}
	}
}
