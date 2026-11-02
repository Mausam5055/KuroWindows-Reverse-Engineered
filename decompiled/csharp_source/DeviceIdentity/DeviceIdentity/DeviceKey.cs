using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DeviceIdentity;

internal static class DeviceKey
{
	internal enum Rung
	{
		PlatformEcdsaP256,
		PlatformRsa2048,
		SoftwareEcdsaP256
	}

	internal sealed class Snapshot : IDisposable
	{
		private readonly object _signLock = new object();

		private readonly AsymmetricAlgorithm _algorithm;

		public Rung Rung { get; }

		internal CngKey CngKey { get; }

		public string AttestationKind { get; }

		public byte[] PublicKeySpki { get; }

		public string PublicKeyBase64 { get; }

		public string KeyId { get; }

		public bool IsRsa => _algorithm is RSA;

		internal Snapshot(Rung rung, CngKey key, AsymmetricAlgorithm algorithm, byte[] spki)
		{
			Rung = rung;
			CngKey = key;
			_algorithm = algorithm;
			PublicKeySpki = spki;
			PublicKeyBase64 = Convert.ToBase64String(spki);
			KeyId = Convert.ToHexString(SHA256.HashData(spki)).ToLowerInvariant();
			AttestationKind = ((rung == Rung.SoftwareEcdsaP256) ? "software" : "tpm");
		}

		public byte[] Sign(ReadOnlySpan<byte> message)
		{
			byte[] data = message.ToArray();
			lock (_signLock)
			{
				AsymmetricAlgorithm algorithm = _algorithm;
				if (!(algorithm is ECDsa eCDsa))
				{
					if (algorithm is RSA rSA)
					{
						return rSA.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
					}
					throw new InvalidOperationException("device key algorithm is neither ECDSA nor RSA");
				}
				return eCDsa.SignData(data, HashAlgorithmName.SHA256);
			}
		}

		internal bool VerifyWithExportedPublicKey(byte[] message, byte[] signature)
		{
			int bytesRead;
			if (_algorithm is ECDsa)
			{
				if (signature.Length != 64)
				{
					return false;
				}
				using ECDsa eCDsa = ECDsa.Create();
				eCDsa.ImportSubjectPublicKeyInfo(PublicKeySpki, out bytesRead);
				return eCDsa.VerifyData(message, signature, HashAlgorithmName.SHA256);
			}
			if (signature.Length != 256)
			{
				return false;
			}
			using RSA rSA = RSA.Create();
			rSA.ImportSubjectPublicKeyInfo(PublicKeySpki, out bytesRead);
			return rSA.VerifyData(message, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		}

		public void Dispose()
		{
			lock (_signLock)
			{
				_algorithm.Dispose();
				CngKey?.Dispose();
			}
		}
	}

	private sealed class RungFailedException : Exception
	{
		public RungFailedException(Rung rung)
			: base("device key rung " + Name(rung) + " failed")
		{
		}
	}

	private sealed class ProviderNotReadyException(Rung rung, string phase, Exception inner) : Exception("device key rung " + Name(rung) + " not ready at " + phase, inner)
	{
		public Rung Rung { get; } = rung;

		public string Phase { get; } = phase;
	}

	private const int NteBadKeyset = -2146893802;

	private const int NteNotFound = -2146893807;

	private const int NteNoKey = -2146893811;

	private const int NteExists = -2146893809;

	private const int NteNotSupported = -2146893783;

	private static readonly HashSet<int> TransientProviderHResults = new HashSet<int> { -2146893776, -2144845816, -2144845815, -2144845814, -2144845813, -2144860160, -2144859894, -2144859872, -2144859870, -2144859869 };

	private static readonly TimeSpan[] NotReadyBackoff = new TimeSpan[5]
	{
		TimeSpan.FromSeconds(5.0),
		TimeSpan.FromSeconds(10.0),
		TimeSpan.FromSeconds(20.0),
		TimeSpan.FromSeconds(40.0),
		TimeSpan.FromSeconds(45.0)
	};

	private static readonly Rung[] AllRungs = new Rung[3]
	{
		Rung.PlatformEcdsaP256,
		Rung.PlatformRsa2048,
		Rung.SoftwareEcdsaP256
	};

	private static readonly Rung[] SoftwareRungOnly = new Rung[1] { Rung.SoftwareEcdsaP256 };

	private static readonly byte[] SelfTestMessage = Encoding.ASCII.GetBytes("kuro-device-key-self-test/v1");

	private static CngProvider? _platformProvider;

	private static readonly object StartLock = new object();

	private static Task<Snapshot?>? _initialisation;

	private static Snapshot? _current;

	private static int _notReadyLogged;

	private static int _uploadProofEnabled;

	private static int _serverHoldsKey;

	private static int _registrationProofDisabled;

	private static readonly object ReopenLock = new object();

	private static CngProvider PlatformProvider => _platformProvider ?? (_platformProvider = new CngProvider(Settings.PlatformCryptoProviderName));

	public static Snapshot? Current => Volatile.Read(in _current);

	public static bool UploadProofEnabled => Volatile.Read(in _uploadProofEnabled) == 1;

	public static bool RegistrationProofEnabled => Volatile.Read(in _registrationProofDisabled) == 0;

	public static void WarmUp()
	{
		Start();
	}

	public static void NoteRegistration(bool signed, string? keyStatus)
	{
		bool flag = signed && keyStatus != null && Settings.DeviceKeyAcknowledgedStatuses.Contains(keyStatus);
		Interlocked.Exchange(ref _serverHoldsKey, flag ? 1 : 0);
		Interlocked.Exchange(ref _uploadProofEnabled, flag ? 1 : 0);
		Log.Auth($"upload proof {(flag ? "on" : "off")} after registration signed={signed} key_status={keyStatus ?? "-"}", "NoteRegistration", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DeviceIdentity\\DeviceKey.cs", 231);
	}

	internal static bool DisableUploadProof()
	{
		return Interlocked.Exchange(ref _uploadProofEnabled, 0) == 1;
	}

	internal static bool ReenableUploadProof(out bool flipped)
	{
		flipped = false;
		if (Volatile.Read(in _serverHoldsKey) != 1)
		{
			return false;
		}
		flipped = Interlocked.Exchange(ref _uploadProofEnabled, 1) == 0;
		return true;
	}

	internal static bool DisableRegistrationProof()
	{
		return Interlocked.Exchange(ref _registrationProofDisabled, 1) == 0;
	}

	internal static bool EnableRegistrationProof()
	{
		return Interlocked.Exchange(ref _registrationProofDisabled, 0) == 1;
	}

	public static async Task<bool> EnsureReadyAsync(TimeSpan budget)
	{
		Task<Snapshot?> initialisation = Start();
		if (!initialisation.IsCompleted && await Task.WhenAny(initialisation, Task.Delay(budget)).ConfigureAwait(continueOnCapturedContext: false) != initialisation)
		{
			if (Interlocked.Exchange(ref _notReadyLogged, 1) == 0)
			{
				Log.Auth($"device key not ready within budget_ms={(int)budget.TotalMilliseconds}; registering unsigned until it is ready", "EnsureReadyAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DeviceIdentity\\DeviceKey.cs", 283);
			}
			return false;
		}
		return await initialisation.ConfigureAwait(continueOnCapturedContext: false) != null;
	}

	private static Task<Snapshot?> Start()
	{
		lock (StartLock)
		{
			return _initialisation ?? (_initialisation = Task.Run((Func<Task<Snapshot>?>)InitialiseAsync));
		}
	}

	internal static Snapshot Reopen(Snapshot failed, Exception cause)
	{
		lock (ReopenLock)
		{
			Snapshot snapshot = Volatile.Read(in _current);
			if (snapshot != null && snapshot != failed)
			{
				return snapshot;
			}
			CngKey cngKey = CngKey.Open(KeyName(failed.Rung), Provider(failed.Rung));
			Snapshot snapshot2;
			try
			{
				snapshot2 = Wrap(failed.Rung, cngKey);
			}
			catch
			{
				cngKey.Dispose();
				throw;
			}
			string text;
			try
			{
				text = SelfTestFailure(snapshot2, "reopen");
			}
			catch
			{
				snapshot2.Dispose();
				throw;
			}
			if (text != null)
			{
				snapshot2.Dispose();
				throw new CryptographicException("self-test " + text);
			}
			Volatile.Write(ref _current, snapshot2);
			failed.Dispose();
			Log.Auth($"device key reopened rung={Name(failed.Rung)} key_id={snapshot2.KeyId} same_key={snapshot2.KeyId == failed.KeyId} after {Describe(cause)}", "Reopen", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DeviceIdentity\\DeviceKey.cs", 353);
			return snapshot2;
		}
	}

	private static async Task<Snapshot?> InitialiseAsync()
	{
		Snapshot snapshot = null;
		try
		{
			snapshot = await LadderAsync().ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (Exception ex)
		{
			Log.Auth("device key unavailable: " + Describe(ex), "InitialiseAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DeviceIdentity\\DeviceKey.cs", 367);
		}
		Volatile.Write(ref _current, snapshot);
		return snapshot;
	}

	private static async Task<Snapshot?> LadderAsync()
	{
		Rung[] rungs = AllRungs;
		if (TpmEndorsement.Tbs.Present == false)
		{
			Log.Auth("device key: no TPM on this machine; using rung=" + Name(Rung.SoftwareEcdsaP256), "LadderAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DeviceIdentity\\DeviceKey.cs", 382);
			rungs = SoftwareRungOnly;
		}
		int run = 0;
		while (true)
		{
			try
			{
				return OpenExistingOrCreate(rungs);
			}
			catch (ProviderNotReadyException ex) when (run < NotReadyBackoff.Length)
			{
				TimeSpan delay = NotReadyBackoff[run];
				Log.Auth($"device key provider not ready rung={Name(ex.Rung)} phase={ex.Phase}: {Describe(ex.InnerException)}; registering unsigned, next try in {delay.TotalSeconds:0}s", "LadderAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DeviceIdentity\\DeviceKey.cs", 396);
				await Task.Delay(delay).ConfigureAwait(continueOnCapturedContext: false);
			}
			catch (ProviderNotReadyException ex2)
			{
				Log.Auth($"device key provider not ready rung={Name(ex2.Rung)} phase={ex2.Phase}: {Describe(ex2.InnerException)}; still not ready after {NotReadyBackoff.Sum((TimeSpan wait) => wait.TotalSeconds):0}s, falling to rung={Name(Rung.SoftwareEcdsaP256)}", "LadderAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DeviceIdentity\\DeviceKey.cs", 401);
				return OpenExistingOrCreate(SoftwareRungOnly);
			}
			run++;
		}
	}

	private static Snapshot? OpenExistingOrCreate(Rung[] rungs)
	{
		ProviderNotReadyException ex = null;
		Snapshot snapshot = null;
		foreach (Rung rung in rungs)
		{
			Snapshot snapshot2 = null;
			bool item;
			bool flag;
			try
			{
				(Snapshot? Key, bool Broken) tuple = TryOpen(rung);
				snapshot2 = tuple.Key;
				item = tuple.Broken;
				flag = snapshot2 != null && SelfTest(snapshot2, "open");
			}
			catch (ProviderNotReadyException ex2)
			{
				snapshot2?.Dispose();
				if (ex == null)
				{
					ex = ex2;
				}
				continue;
			}
			if (snapshot2 == null && !item)
			{
				continue;
			}
			if ((snapshot2 != null) & flag)
			{
				Log.Auth($"device key opened rung={Name(rung)} attestation={snapshot2.AttestationKind} key_id={snapshot2.KeyId}", "OpenExistingOrCreate", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DeviceIdentity\\DeviceKey.cs", 444);
				if (rung != Rung.SoftwareEcdsaP256)
				{
					DeleteSoftwareStandIn();
					return snapshot2;
				}
				if (rungs.Length == 1 || ex != null)
				{
					return snapshot2;
				}
				snapshot = snapshot2;
				break;
			}
			if (snapshot2 != null)
			{
				DeleteQuietly(snapshot2.CngKey, rung);
				snapshot2.Dispose();
			}
			if (ex != null)
			{
				throw ex;
			}
			return CreateFrom((rung == Rung.SoftwareEcdsaP256) ? rung : Next(rung));
		}
		if (ex != null)
		{
			throw ex;
		}
		if (snapshot == null)
		{
			return CreateFrom(rungs[0]);
		}
		Snapshot snapshot3;
		try
		{
			snapshot3 = CreateFrom(rungs[0]);
		}
		catch (ProviderNotReadyException ex3)
		{
			Log.Auth($"device key provider not ready rung={Name(ex3.Rung)} phase={ex3.Phase}: {Describe(ex3.InnerException)}; keeping the software stand-in this launch", "OpenExistingOrCreate", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DeviceIdentity\\DeviceKey.cs", 493);
			return snapshot;
		}
		if (snapshot3 == null || snapshot3.Rung == Rung.SoftwareEcdsaP256)
		{
			snapshot3?.Dispose();
			return snapshot;
		}
		snapshot.Dispose();
		DeleteSoftwareStandIn();
		return snapshot3;
	}

	private static (Snapshot? Key, bool Broken) TryOpen(Rung rung)
	{
		CngKey cngKey = null;
		try
		{
			cngKey = CngKey.Open(KeyName(rung), Provider(rung));
		}
		catch (CryptographicException ex) when (((Func<bool>)delegate
		{
			// Could not convert BlockContainer to single expression
			int hResult = ex.HResult;
			return (hResult == -2146893811 || hResult == -2146893807 || hResult == -2146893802) ? true : false;
		}).Invoke())
		{
			Log.Auth($"device key open rung={Name(rung)}: no key ({Describe(ex)})", "TryOpen", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DeviceIdentity\\DeviceKey.cs", 521);
			return (Key: null, Broken: false);
		}
		catch (Exception ex2) when (IsProviderNotReady(rung, ex2))
		{
			throw new ProviderNotReadyException(rung, "open", ex2);
		}
		catch (Exception ex3)
		{
			Log.Auth("device key open rung=" + Name(rung) + " failed: " + Describe(ex3), "TryOpen", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DeviceIdentity\\DeviceKey.cs", 531);
			return (Key: null, Broken: false);
		}
		try
		{
			return (Key: Wrap(rung, cngKey), Broken: false);
		}
		catch (Exception ex4) when (IsProviderNotReady(rung, ex4))
		{
			cngKey.Dispose();
			throw new ProviderNotReadyException(rung, "wrap", ex4);
		}
		catch (Exception ex5)
		{
			Log.Auth($"device key open rung={Name(rung)} failed to wrap: {Describe(ex5)}; deleting and recreating", "TryOpen", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DeviceIdentity\\DeviceKey.cs", 548);
			DeleteQuietly(cngKey, rung);
			cngKey.Dispose();
			return (Key: null, Broken: true);
		}
	}

	private static Snapshot? CreateFrom(Rung rung)
	{
		while (true)
		{
			bool flag = false;
			Snapshot snapshot;
			try
			{
				snapshot = Create(rung);
			}
			catch (CryptographicException ex) when (ex.HResult == -2146893809)
			{
				Snapshot item = TryOpen(rung).Key;
				if (item == null)
				{
					if (rung == Rung.SoftwareEcdsaP256)
					{
						Log.Auth("device key create rung=" + Name(rung) + " answered NTE_EXISTS but the key cannot be opened; no rung left, registering unsigned", "CreateFrom", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DeviceIdentity\\DeviceKey.cs", 574);
						return null;
					}
					Log.Auth("device key create rung=" + Name(rung) + " answered NTE_EXISTS but the key cannot be opened; falling to rung=" + Name(Next(rung)), "CreateFrom", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DeviceIdentity\\DeviceKey.cs", 578);
					rung = Next(rung);
					continue;
				}
				snapshot = item;
				flag = true;
			}
			catch (Exception ex2) when (IsProviderNotReady(rung, ex2))
			{
				throw new ProviderNotReadyException(rung, "create", ex2);
			}
			catch (Exception ex3)
			{
				if (rung == Rung.SoftwareEcdsaP256)
				{
					Log.Auth($"device key create rung={Name(rung)} failed: {Describe(ex3)}; no rung left, registering unsigned", "CreateFrom", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DeviceIdentity\\DeviceKey.cs", 594);
					return null;
				}
				Rung rung2 = ((rung == Rung.PlatformEcdsaP256 && ((ex3 is CryptographicException && ex3.HResult == -2146893783) || ex3 is RungFailedException)) ? Rung.PlatformRsa2048 : Rung.SoftwareEcdsaP256);
				Log.Auth($"device key create rung={Name(rung)} failed: {Describe(ex3)}; falling to rung={Name(rung2)}", "CreateFrom", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DeviceIdentity\\DeviceKey.cs", 607);
				rung = rung2;
				continue;
			}
			bool flag2;
			string text;
			try
			{
				flag2 = SelfTest(snapshot, "create");
				text = ((flag2 && !flag) ? ReopenFailure(rung) : null);
			}
			catch
			{
				snapshot.Dispose();
				throw;
			}
			if (flag2 && text == null)
			{
				Log.Auth(flag ? $"device key opened rung={Name(rung)} attestation={snapshot.AttestationKind} key_id={snapshot.KeyId} (existed at create)" : $"device key created rung={Name(rung)} attestation={snapshot.AttestationKind} key_id={snapshot.KeyId}", "CreateFrom", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DeviceIdentity\\DeviceKey.cs", 631);
				return snapshot;
			}
			if (text != null)
			{
				Log.Auth((rung == Rung.SoftwareEcdsaP256) ? $"device key created rung={Name(rung)} key_id={snapshot.KeyId} cannot be opened by name: {text}; deleting, no rung left, registering unsigned" : $"device key created rung={Name(rung)} key_id={snapshot.KeyId} cannot be opened by name: {text}; deleting and falling to rung={Name(Rung.SoftwareEcdsaP256)}", "CreateFrom", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DeviceIdentity\\DeviceKey.cs", 639);
			}
			DeleteQuietly(snapshot.CngKey, rung);
			snapshot.Dispose();
			if (rung == Rung.SoftwareEcdsaP256)
			{
				break;
			}
			rung = ((text != null) ? Rung.SoftwareEcdsaP256 : Next(rung));
		}
		return null;
	}

	private static Snapshot Create(Rung rung)
	{
		CngKeyCreationParameters cngKeyCreationParameters = new CngKeyCreationParameters
		{
			Provider = Provider(rung),
			KeyUsage = CngKeyUsages.Signing,
			ExportPolicy = CngExportPolicies.None,
			KeyCreationOptions = CngKeyCreationOptions.None
		};
		CngKey cngKey;
		if (rung == Rung.PlatformRsa2048)
		{
			cngKeyCreationParameters.Parameters.Add(new CngProperty("Length", BitConverter.GetBytes(2048), CngPropertyOptions.None));
			cngKey = CngKey.Create(CngAlgorithm.Rsa, KeyName(rung), cngKeyCreationParameters);
		}
		else
		{
			cngKey = CngKey.Create(CngAlgorithm.ECDsaP256, KeyName(rung), cngKeyCreationParameters);
		}
		try
		{
			return Wrap(rung, cngKey);
		}
		catch (Exception ex) when (IsProviderNotReady(rung, ex))
		{
			cngKey.Dispose();
			throw new ProviderNotReadyException(rung, "wrap", ex);
		}
		catch (Exception ex2)
		{
			Log.Auth("device key create rung=" + Name(rung) + " produced a key that cannot sign: " + Describe(ex2), "Create", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DeviceIdentity\\DeviceKey.cs", 685);
			DeleteQuietly(cngKey, rung);
			cngKey.Dispose();
			throw new RungFailedException(rung);
		}
	}

	private static Snapshot Wrap(Rung rung, CngKey key)
	{
		if (rung == Rung.PlatformRsa2048)
		{
			RSACng rSACng = new RSACng(key);
			return new Snapshot(rung, key, rSACng, rSACng.ExportSubjectPublicKeyInfo());
		}
		ECDsaCng eCDsaCng = new ECDsaCng(key);
		return new Snapshot(rung, key, eCDsaCng, eCDsaCng.ExportSubjectPublicKeyInfo());
	}

	private static bool SelfTest(Snapshot snapshot, string phase)
	{
		string text = SelfTestFailure(snapshot, phase);
		if (text == null)
		{
			return true;
		}
		Log.Auth($"device key self-test failed phase={phase} rung={Name(snapshot.Rung)} reason={text}; deleting and recreating", "SelfTest", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DeviceIdentity\\DeviceKey.cs", 712);
		return false;
	}

	private static string? SelfTestFailure(Snapshot snapshot, string phase)
	{
		try
		{
			byte[] array = snapshot.Sign(SelfTestMessage);
			return snapshot.VerifyWithExportedPublicKey(SelfTestMessage, array) ? null : $"verify_false signature_bytes={array.Length}";
		}
		catch (Exception ex) when (IsProviderNotReady(snapshot.Rung, ex))
		{
			throw new ProviderNotReadyException(snapshot.Rung, "self-test-" + phase, ex);
		}
		catch (Exception ex2)
		{
			return Describe(ex2);
		}
	}

	private static string? ReopenFailure(Rung rung)
	{
		try
		{
			using (CngKey.Open(KeyName(rung), Provider(rung)))
			{
				return null;
			}
		}
		catch (Exception ex) when (IsProviderNotReady(rung, ex))
		{
			throw new ProviderNotReadyException(rung, "reopen", ex);
		}
		catch (Exception ex2)
		{
			return Describe(ex2);
		}
	}

	private static void DeleteQuietly(CngKey key, Rung rung)
	{
		try
		{
			key.Delete();
		}
		catch (Exception ex)
		{
			Log.Auth("device key delete rung=" + Name(rung) + " failed: " + Describe(ex), "DeleteQuietly", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DeviceIdentity\\DeviceKey.cs", 769);
		}
	}

	private static void DeleteSoftwareStandIn()
	{
		try
		{
			if (!CngKey.Exists(KeyName(Rung.SoftwareEcdsaP256), Provider(Rung.SoftwareEcdsaP256)))
			{
				return;
			}
			using CngKey cngKey = CngKey.Open(KeyName(Rung.SoftwareEcdsaP256), Provider(Rung.SoftwareEcdsaP256));
			cngKey.Delete();
			Log.Auth("device key deleted the software stand-in rung=" + Name(Rung.SoftwareEcdsaP256), "DeleteSoftwareStandIn", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DeviceIdentity\\DeviceKey.cs", 787);
		}
		catch (Exception ex)
		{
			Log.Auth("device key delete rung=" + Name(Rung.SoftwareEcdsaP256) + " failed: " + Describe(ex), "DeleteSoftwareStandIn", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DeviceIdentity\\DeviceKey.cs", 791);
		}
	}

	private static Rung Next(Rung rung)
	{
		if (rung == Rung.PlatformEcdsaP256)
		{
			return Rung.PlatformRsa2048;
		}
		return Rung.SoftwareEcdsaP256;
	}

	private static string KeyName(Rung rung)
	{
		if (rung != Rung.PlatformRsa2048)
		{
			return Settings.DeviceKeyName;
		}
		return Settings.DeviceKeyRsaName;
	}

	private static CngProvider Provider(Rung rung)
	{
		if (rung != Rung.SoftwareEcdsaP256)
		{
			return PlatformProvider;
		}
		return CngProvider.MicrosoftSoftwareKeyStorageProvider;
	}

	internal static string Name(Rung rung)
	{
		return rung switch
		{
			Rung.PlatformEcdsaP256 => "platform_ecdsa_p256", 
			Rung.PlatformRsa2048 => "platform_rsa_2048", 
			_ => "software_ecdsa_p256", 
		};
	}

	private static string Describe(Exception ex)
	{
		if (!(ex is RungFailedException))
		{
			return $"{ex.GetType().Name}: {ex.Message} hr=0x{ex.HResult:X8}";
		}
		return "rung_failed";
	}

	private static bool IsProviderNotReady(Rung rung, Exception ex)
	{
		if (rung != Rung.SoftwareEcdsaP256)
		{
			return TransientProviderHResults.Contains(ex.HResult);
		}
		return false;
	}
}
