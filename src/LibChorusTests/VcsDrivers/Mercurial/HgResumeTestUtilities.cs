﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using Chorus.VcsDrivers;
using Chorus.VcsDrivers.Mercurial;
using Palaso.Progress;
using Palaso.Progress.LogBox;
using Palaso.TestUtilities;

namespace LibChorus.Tests.VcsDrivers.Mercurial
{
	internal enum ApiServerType
	{
		Dummy,
		Pull,
		Push
	}


	internal class TestEnvironment : IDisposable
	{

		public TestEnvironment(string testName, ApiServerType type)
		{
			Progress = new ProgressForTest();
			Local = new RepositorySetup(testName + "-local");
			Remote = new RepositorySetup(testName + "-remote");
			switch (type)
			{
				case ApiServerType.Dummy:
					ApiServer = new DummyApiServerForTest(testName);
					break;
				case ApiServerType.Pull:
					ApiServer = new PullHandlerApiServerForTest(Remote.Repository);
					break;
				case ApiServerType.Push:
					ApiServer = new PushHandlerApiServerForTest(Remote.Repository);
					break;
			}
			Transport = new HgResumeTransport(Local.Repository, testName, ApiServer, new MultiProgress(new IProgress[] { new Palaso.Progress.LogBox.ConsoleProgress { ShowVerbose = true }, Progress }));
		}

		public HgResumeTransport Transport { get; private set; }
		public RepositorySetup Local { get; private set; }
		public RepositorySetup Remote { get; private set; }
		public IApiServerForTest ApiServer { get; private set; }
		public ProgressForTest Progress { get; private set; }

		public void Dispose()
		{
			Transport.RemoveCache();
			Local.Dispose();
			Remote.Dispose();
		}

		public void LocalCheckIn()
		{
			string filename = Path.GetRandomFileName();
			Local.AddAndCheckinFile(filename, "localcheckin");
		}

		public void RemoteCheckIn()
		{
			string filename = Path.GetRandomFileName();
			Remote.AddAndCheckinFile(filename, "remotecheckin");
		}

		public void LocalCheckInLargeFile()
		{
			LocalCheckInLargeFile(1);
		}

		public void LocalCheckInLargeFile(int sizeInMB)
		{
			var filePath = Local.ProjectFolder.GetPathForNewTempFile(false);
			byte[] data = new byte[sizeInMB * 1024 * 1024];  // MB file
			Random rng = new Random();
			rng.NextBytes(data);
			File.WriteAllBytes(filePath, data);
			Local.Repository.AddAndCheckinFile(filePath);
		}

		public void RemoteCheckInLargeFile()
		{
			RemoteCheckInLargeFile(1);
		}

		public void RemoteCheckInLargeFile(int sizeInMB)
		{
			var filePath = Remote.ProjectFolder.GetPathForNewTempFile(false);
			byte[] data = new byte[sizeInMB * 1024 * 1024];  // 5MB file
			Random rng = new Random();
			rng.NextBytes(data);
			File.WriteAllBytes(filePath, data);
			Remote.Repository.AddAndCheckinFile(filePath);
		}

		public void SyncRemoteFromLocal()
		{
			var address = new HttpRepositoryPath("localrepo", Local.Repository.PathToRepo, false);
			Remote.Repository.Pull(address, Local.Repository.PathToRepo);
			Remote.Repository.Update();
		}

		public void CloneRemoteFromLocal()
		{
			//Remote.Repository.Init();
			var address = new HttpRepositoryPath("localrepo", Local.Repository.PathToRepo, false);
			Remote.Repository.Pull(address, Local.Repository.PathToRepo);
			Remote.Repository.Update();
		}
	}




	public interface IApiServerForTest : IApiServer
	{
		void AddTimeoutResponse(int executeCount);
		void AddServerUnavailableResponse(int executeCount, string serverMessage);
		void AddFailResponse(int executeCount);
		void PrepareBundle(string revHash);
		void AddResponse(HgResumeApiResponse response);
		void AddTimeOut();
		string StoragePath { get; }
		string CurrentTip { get; }
	}

	internal class DummyApiServerForTest : IApiServerForTest, IDisposable
	{
		private readonly Queue<HgResumeApiResponse> _responseQueue = new Queue<HgResumeApiResponse>();

		public DummyApiServerForTest() :
			this("DummyApiServerForTest")
		{
		}

		public DummyApiServerForTest(string identifier)
		{
			Host = identifier;
			ProjectId = "SampleProject";
		}

		public HgResumeApiResponse Execute(string method, IDictionary<string, string> parameters, int secondsBeforeTimeout)
		{
			return Execute(method, parameters, new byte[0], secondsBeforeTimeout);
		}

		public HgResumeApiResponse Execute(string method, IDictionary<string, string> parameters, byte[] contentToSend, int secondsBeforeTimeout)
		{
			HgResumeApiResponse response;
			if (_responseQueue.Count > 0)
			{
				response = _responseQueue.Dequeue();
				if (response.StatusCode == HttpStatusCode.RequestTimeout)
				{
					return null;
				}
			} else
			{
				response = new HgResumeApiResponse {StatusCode = HttpStatusCode.InternalServerError};
			}
			response.ResponseTimeInMilliseconds = 200;
			return response;
		}

		public string Host { get; private set; }

		public string ProjectId { get; set; }

		public string Url
		{
			get { return "fake api server"; }
		}

		public void AddTimeoutResponse(int executeCount)
		{
			throw new NotImplementedException();
		}

		public void AddServerUnavailableResponse(int executeCount, string serverMessage)
		{
			throw new NotImplementedException();
		}

		public void AddFailResponse(int executeCount)
		{
			throw new NotImplementedException();
		}

		public void PrepareBundle(string revHash)
		{
			throw new NotImplementedException();
		}

		public void AddResponse(HgResumeApiResponse response)
		{
			_responseQueue.Enqueue(response);
		}

		public void AddTimeOut()
		{
			// we are hijacking the HTTP 408 request timeout to mean a client-side networking timeout...
			// it works for our testing purposes even though that's not what the status code means
			_responseQueue.Enqueue(new HgResumeApiResponse {StatusCode = HttpStatusCode.RequestTimeout});
		}

		public string StoragePath
		{
			get { throw new NotImplementedException(); }
		}

		public string CurrentTip
		{
			get { throw new NotImplementedException(); }
		}

		public void Dispose()
		{
		}
	}

	internal class ServerUnavailableResponse
	{
		public string Message;
		public int ExecuteCount;
	}

	internal class PushHandlerApiServerForTest : IApiServerForTest, IDisposable
	{


		private PullStorageManager _helper;  // yes, we DO want to use the PullStorageManager for the PushHandler (this is the other side of the API, so it's opposite)
		private readonly HgRepository _repo;
		private TemporaryFolder _localStorage;
		private int _executeCount;
		private List<int> _timeoutList;
		private List<ServerUnavailableResponse> _serverUnavailableList;
		private int _failCount;

		public PushHandlerApiServerForTest(HgRepository repo)
		{
			const string identifier = "PushHandlerApiServerForTest";
			_localStorage = new TemporaryFolder(identifier);
			_repo = repo;
			Host = identifier;
			ProjectId = "SampleProject";
			_executeCount = 0;
			_failCount = -1;
			_timeoutList = new List<int>();
			_serverUnavailableList = new List<ServerUnavailableResponse>();
		}
		public HgResumeApiResponse Execute(string method, IDictionary<string, string> parameters, int secondsBeforeTimeout)
		{
			return Execute(method, parameters, new byte[0], secondsBeforeTimeout);
		}

		public HgResumeApiResponse Execute(string method, IDictionary<string, string> parameters, byte[] bytesToWrite, int secondsBeforeTimeout)
		{
			if (method == "getRevisions")
			{
				IEnumerable<string> revisions = _repo.GetAllRevisions().Select(rev => rev.Number.Hash);
				return ApiResponses.Revisions(string.Join("|", revisions.ToArray()));
			}
			if (method == "finishPushBundle")
			{
				return ApiResponses.PushComplete();
			}
			if (method == "pushBundleChunk")
			{
				_executeCount++;
				if (_failCount == _executeCount)
				{
					return ApiResponses.Failed("");
				}
				if (_timeoutList.Contains(_executeCount))
				{
					return null;
				}
				if (_serverUnavailableList.Any(i => i.ExecuteCount == _executeCount))
				{
					return ApiResponses.NotAvailable(
							_serverUnavailableList.Where(i => i.ExecuteCount == _executeCount).First().Message
							);
				}
				_helper = new PullStorageManager(_localStorage.Path, parameters["transId"]);

				int bundleSize = Convert.ToInt32(parameters["bundleSize"]);
				int offset = Convert.ToInt32(parameters["offset"]);
				int chunkSize = bytesToWrite.Length;

				_helper.WriteChunk(offset, bytesToWrite);

				if (offset + chunkSize == bundleSize)
				{
					if (_repo.Unbundle(_helper.BundlePath))
					{
						return ApiResponses.PushComplete();
					}
					return ApiResponses.Reset();
				}
				if (offset + chunkSize < bundleSize)
				{
					return ApiResponses.PushAccepted(_helper.StartOfWindow);
				}
				return ApiResponses.Failed("offset + chunkSize > bundleSize !");
			}
			return ApiResponses.Custom(HttpStatusCode.InternalServerError);
		}

		public string Host { get; private set; }

		public string ProjectId { get; set; }

		public void AddTimeOut()
		{
			throw new NotImplementedException();
		}

		public string StoragePath
		{
			get { return _localStorage.Path; }
		}

		public string CurrentTip
		{
			get { throw new NotImplementedException(); }
		}

		public string Url
		{
			get { return "fake api server"; }
		}

		public void Dispose()
		{
			_localStorage.Dispose();
		}

		public void AddTimeoutResponse(int executeCount)
		{
			if (!_timeoutList.Contains(executeCount))
			{
				_timeoutList.Add(executeCount);
			}
		}

		public void AddServerUnavailableResponse(int executeCount, string serverMessage)
		{
			if (!_serverUnavailableList.Any(i => i.ExecuteCount == executeCount))
			{
				_serverUnavailableList.Add(new ServerUnavailableResponse {ExecuteCount = executeCount, Message = serverMessage});
			}
		}

		public void AddFailResponse(int executeCount)
		{
			_failCount = executeCount;
		}

		public void PrepareBundle(string revHash)
		{
			throw new NotImplementedException();
		}

		public void AddResponse(HgResumeApiResponse response)
		{
			throw new NotImplementedException();
		}
	}

	internal class PullHandlerApiServerForTest : IApiServerForTest, IDisposable
	{
		private readonly PushStorageManager _helper;
		private readonly HgRepository _repo;
		private int _executeCount;
		private int _failCount;
		private List<int> _timeoutList;
		private List<ServerUnavailableResponse> _serverUnavailableList;
		private TemporaryFolder _storageFolder;
		public string OriginalTip;

		public PullHandlerApiServerForTest(HgRepository repo)
		{
			const string identifier = "PullHandlerApiServerForTest";
			_storageFolder = new TemporaryFolder(identifier);
			_repo = repo;
			_helper = new PushStorageManager(_storageFolder.Path, "randomHash");
			Host = identifier;
			ProjectId = "SampleProject";
			_executeCount = 0;
			_failCount = -1;
			_timeoutList = new List<int>();
			_serverUnavailableList = new List<ServerUnavailableResponse>();
			OriginalTip = "";
		}

		private string CurrentTip
		{
			get { return _repo.GetTip().Number.Hash; }
		}

		public void AddTimeOut()
		{
			throw new NotImplementedException();
		}

		public string StoragePath
		{
			get { return _storageFolder.Path; }
		}

		string IApiServerForTest.CurrentTip
		{
			get { return CurrentTip; }
		}

		public void PrepareBundle(string revHash)
		{
			if(File.Exists(_helper.BundlePath))
			{
				File.Delete(_helper.BundlePath);
			}
			_repo.MakeBundle(revHash, _helper.BundlePath);
			OriginalTip = CurrentTip;
		}

		public void AddResponse(HgResumeApiResponse response)
		{
			throw new NotImplementedException();
		}

		public HgResumeApiResponse Execute(string method, IDictionary<string, string> parameters, int secondsBeforeTimeout)
		{
			return Execute(method, parameters, new byte[0], secondsBeforeTimeout);
		}

		public HgResumeApiResponse Execute(string method, IDictionary<string, string> parameters, byte[] bytesToWrite, int secondsBeforeTimeout)
		{
			_executeCount++;
			if (method == "finishPullBundle")
			{
				if (CurrentTip != OriginalTip)
				{
					PrepareBundle(OriginalTip);
					return ApiResponses.Reset(); // repo changed in between pulls
				}
				return ApiResponses.Custom(HttpStatusCode.OK);
			}
			if (method == "getRevisions")
			{
				IEnumerable<string> revisions = _repo.GetAllRevisions().Select(rev => rev.Number.Hash);
				return ApiResponses.Revisions(string.Join("|", revisions.ToArray()));
			}
			if (method == "pullBundleChunk")
			{
				if (_failCount == _executeCount)
				{
					return ApiResponses.Failed("");
				}
				if (_timeoutList.Contains(_executeCount))
				{
					return null;
				}
				if (_serverUnavailableList.Any(i => i.ExecuteCount == _executeCount))
				{
					return ApiResponses.NotAvailable(
							_serverUnavailableList.Where(i => i.ExecuteCount == _executeCount).First().Message
							);
				}
				if (parameters["baseHash"] == CurrentTip)
				{
					return ApiResponses.PullNoChange();
				}


				var bundleFileInfo = new FileInfo(_helper.BundlePath);
				if (bundleFileInfo.Exists && bundleFileInfo.Length == 0  || !bundleFileInfo.Exists)
				{
					PrepareBundle(parameters["baseHash"]);
				}
				int offset = Convert.ToInt32(parameters["offset"]);
				int chunkSize = Convert.ToInt32(parameters["chunkSize"]);
				var bundleFile = new FileInfo(_helper.BundlePath);
				if (offset >= bundleFile.Length)
				{
					return ApiResponses.Failed("offset greater than bundleSize");
				}
				var chunk = _helper.GetChunk(offset, chunkSize);
				return ApiResponses.PullOk(Convert.ToInt32(bundleFile.Length), chunk);
			}
			return ApiResponses.Custom(HttpStatusCode.InternalServerError);
		}

		public string Host { get; private set; }

		public string ProjectId { get; set; }

		public string Url
		{
			get { return "fake api server"; }
		}

		public void Dispose()
		{
			_storageFolder.Dispose();
		}

		public void AddTimeoutResponse(int executeCount)
		{
			if (!_timeoutList.Contains(executeCount))
			{
				_timeoutList.Add(executeCount);
			}
		}

		public void AddServerUnavailableResponse(int executeCount, string serverMessage)
		{
			if (!_serverUnavailableList.Any(i => i.ExecuteCount == executeCount))
			{
				_serverUnavailableList.Add(new ServerUnavailableResponse { ExecuteCount = executeCount, Message = serverMessage });
			}
		}

		public void AddFailResponse(int executeCount)
		{
			_failCount = executeCount;
		}
	}


	internal class ProgressForTest : IProgress, IDisposable
	{
		public List<string> Statuses = new List<string>();
		public List<string> Messages = new List<string>();
		public List<string> Warnings = new List<string>();
		public List<Exception> Exceptions = new List<Exception>();
		public List<string> Errors = new List<string>();
		public List<string> Verbose = new List<string>();
		private List<string> _all = new List<string>();

		public void WriteStatus(string message, params object[] args)
		{
			Statuses.Add(message);
			_all.Add(message);
		}

		public void WriteMessage(string message, params object[] args)
		{
			Messages.Add(message);
			_all.Add(message);
		}

		public void WriteMessageWithColor(string colorName, string message, params object[] args)
		{
			Messages.Add(message);
			_all.Add(message);
		}

		public void WriteWarning(string message, params object[] args)
		{
			Warnings.Add(message);
			_all.Add(message);
		}

		public void WriteException(Exception error)
		{
			Exceptions.Add(error);
			_all.Add(error.Message);
		}

		public void WriteError(string message, params object[] args)
		{
			Errors.Add(message);
			_all.Add(message);
		}

		public void WriteVerbose(string message, params object[] args)
		{
			Verbose.Add(message);
			_all.Add(message);
		}

		public bool ShowVerbose
		{
			set { }
		}

		public List<string> AllMessages
		{
			get
			{
				return _all;
			}
		}

		public string AllMessagesString()
		{
			return String.Join(" ", _all.ToArray());
		}

		public bool CancelRequested
		{
			get { return false; }
			set { }
		}

		public bool ErrorEncountered
		{
			get { return false; }
			set { }
		}

		public IProgressIndicator ProgressIndicator
		{
			get;
			set;
		}
		public SynchronizationContext SyncContext { get; set; }

		public void Dispose()
		{
		}
	}

	public class ApiResponses
	{
		public static HgResumeApiResponse PushComplete()
		{
			return new HgResumeApiResponse
			{
				StatusCode = HttpStatusCode.OK,
				Headers = new Dictionary<string, string>
										 {
											 {"X-HgR-Status", "SUCCESS"},
											 {"X-HgR-Version", "1"}
										 },
				ResponseTimeInMilliseconds = 200
			};
		}

		public static HgResumeApiResponse PushAccepted(int startOfWindow)
		{
			return new HgResumeApiResponse
			{
				StatusCode = HttpStatusCode.Accepted,
				Headers = new Dictionary<string, string>
										 {
											 {"X-HgR-Status", "RECEIVED"},
											 {"X-HgR-Version", "1"},
											 {"X-HgR-Sow", startOfWindow.ToString()}
										 },
				ResponseTimeInMilliseconds = 200
			};
		}

		public static HgResumeApiResponse Reset()
		{
			return new HgResumeApiResponse
			{
				StatusCode = HttpStatusCode.BadRequest,
				Headers = new Dictionary<string, string>
										 {
											 {"X-HgR-Status", "RESET"},
											 {"X-HgR-Version", "1"}
										 },
				ResponseTimeInMilliseconds = 200
			};
		}


		public static HgResumeApiResponse Failed(string message)
		{
			var response = new HgResumeApiResponse
			{
				StatusCode = HttpStatusCode.BadRequest,
				Headers = new Dictionary<string, string>
										 {
											 {"X-HgR-Status", "FAIL"},
											 {"X-HgR-Version", "1"}
										 },
				ResponseTimeInMilliseconds = 200
			};
			if (!String.IsNullOrEmpty(message))
			{
				response.Headers.Add("X-HgR-Error", message);
			}
			return response;
		}

		public static HgResumeApiResponse Custom(HttpStatusCode status)
		{
			return new HgResumeApiResponse
					   {
						   StatusCode = status,
						   Headers = new Dictionary<string, string>(),
						   Content = new byte[0],
						   ResponseTimeInMilliseconds = 200
			};
		}

		public static HgResumeApiResponse Revisions(string revisions)
		{
			return new HgResumeApiResponse
			{
				StatusCode = HttpStatusCode.OK,
				Headers = new Dictionary<string, string>
										 {
											 {"X-HgR-Status", "SUCCESS"},
											 {"X-HgR-Version", "1"}
										 },
				Content = Encoding.UTF8.GetBytes(revisions),
				ResponseTimeInMilliseconds = 200
			};
		}

		public static HgResumeApiResponse PullOk(int bundleSize, byte[] contentToSend)
		{
			if (contentToSend.Length > bundleSize)
			{
				throw new ArgumentException("bundleSize must be larger than the size of the content you are sending");
			}

			return new HgResumeApiResponse
			{
				StatusCode = HttpStatusCode.OK,
				Headers = new Dictionary<string, string>
										 {
											 {"X-HgR-Status", "SUCCESS"},
											 {"X-HgR-Version", "1"},
											 {"X-HgR-BundleSize", bundleSize.ToString()},
											 {"X-HgR-ChunkSize", contentToSend.Length.ToString()}
										 },
				Content = contentToSend,
				ResponseTimeInMilliseconds = 200
			};
		}

		public static HgResumeApiResponse PullNoChange()
		{
			return new HgResumeApiResponse
			{
				StatusCode = HttpStatusCode.NotModified,
				Headers = new Dictionary<string, string>
										 {
											 {"X-HgR-Status", "NOCHANGE"},
											 {"X-HgR-Version", "1"}
										 },
				ResponseTimeInMilliseconds = 200
			};
		}

		public static HgResumeApiResponse NotAvailable(string message)
		{
			return new HgResumeApiResponse
			{
				StatusCode = HttpStatusCode.ServiceUnavailable,
				Headers = new Dictionary<string, string>
										 {
											 {"X-HgR-Status", "NOTAVAILABLE"},
											 {"X-HgR-Version", "1"}
										 },
				Content = Encoding.UTF8.GetBytes(message),
				ResponseTimeInMilliseconds = 200
			};
		}
	}
}