using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using SharpGen.Runtime;
using Vortice;
using Vortice.D3DCompiler;
using Vortice.DXGI;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace KuroWindows.Capture.Gen2;

internal sealed class GpuEncoderContext : IDisposable
{
	private sealed class SampleReleaseCallback(Action<nint> released) : CallbackBase, IMFAsyncCallback, IUnknown, ICallbackable, IDisposable
	{
		public Result GetParameters(out AsyncCallbackFlags flags, out int queue)
		{
			flags = AsyncCallbackFlags.None;
			queue = 5;
			return Result.Ok;
		}

		public Result Invoke(IMFAsyncResult asyncResult)
		{
			nint obj = IntPtr.Zero;
			if (asyncResult.GetObject() is ComObject comObject)
			{
				obj = comObject.QueryInterfaceOrNull(typeof(IMFSample).GUID);
				comObject.Dispose();
			}
			released(obj);
			return Result.Ok;
		}
	}

	public const int PoolSize = 8;

	private static readonly Guid MftEnumAdapterLuid = new Guid("1d39518c-e220-4da8-a07f-ba172552d6b1");

	private const uint MftEnumFlagAsync = 2u;

	private const uint MftEnumFlagHardware = 4u;

	private const uint MftEnumFlagSortAndFilter = 64u;

	private readonly ID3D11Device _device;

	private readonly ID3D11DeviceContext _context;

	private readonly ID3D11Multithread _multithread;

	private readonly IMFDXGIDeviceManager _deviceManager;

	private readonly ID3D11ComputeShader _convertShader;

	private readonly ID3D11ComputeShader _cursorShader;

	private readonly ID3D11SamplerState _linearSampler;

	private readonly ID3D11Texture2D[] _nv12Textures = new ID3D11Texture2D[8];

	private readonly ID3D11UnorderedAccessView[] _yViews = new ID3D11UnorderedAccessView[8];

	private readonly ID3D11UnorderedAccessView[] _uvViews = new ID3D11UnorderedAccessView[8];

	private readonly IMFSample[] _samples = new IMFSample[8];

	private readonly Dictionary<nint, int> _slotBySamplePointer = new Dictionary<nint, int>(8);

	private readonly ConcurrentQueue<int> _freeSlots = new ConcurrentQueue<int>();

	private readonly SemaphoreSlim _poolGate = new SemaphoreSlim(8, 8);

	private readonly SampleReleaseCallback _releaseCallback;

	private readonly int[] _slotReturnableByCaller = new int[8];

	private int _userCount = 1;

	private bool _disposed;

	public int Width { get; }

	public int Height { get; }

	public string HardwareMftName { get; }

	public string AdapterName { get; }

	public long AdapterLuid { get; }

	public ID3D11Device Device => _device;

	public ID3D11DeviceContext ImmediateContext => _context;

	public ID3D11ComputeShader CursorBlendShader => _cursorShader;

	private GpuEncoderContext(ID3D11Device device, ID3D11DeviceContext context, ID3D11Multithread multithread, IMFDXGIDeviceManager deviceManager, ID3D11ComputeShader convertShader, ID3D11ComputeShader cursorShader, ID3D11SamplerState linearSampler, int width, int height, string hardwareMftName, string adapterName, long adapterLuid)
	{
		_device = device;
		_context = context;
		_multithread = multithread;
		_deviceManager = deviceManager;
		_convertShader = convertShader;
		_cursorShader = cursorShader;
		_linearSampler = linearSampler;
		Width = width;
		Height = height;
		HardwareMftName = hardwareMftName;
		AdapterName = adapterName;
		AdapterLuid = adapterLuid;
		_releaseCallback = new SampleReleaseCallback(OnSampleReleased);
		try
		{
			CreatePool(device, width, height);
		}
		catch
		{
			for (int i = 0; i < 8; i++)
			{
				_samples[i]?.Dispose();
				_yViews[i]?.Dispose();
				_uvViews[i]?.Dispose();
				_nv12Textures[i]?.Dispose();
			}
			_releaseCallback.Dispose();
			throw;
		}
	}

	private void CreatePool(ID3D11Device device, int width, int height)
	{
		for (int i = 0; i < 8; i++)
		{
			_nv12Textures[i] = device.CreateTexture2D(new Texture2DDescription
			{
				Width = (uint)width,
				Height = (uint)height,
				MipLevels = 1u,
				ArraySize = 1u,
				Format = Format.NV12,
				SampleDescription = new SampleDescription(1u, 0u),
				Usage = ResourceUsage.Default,
				BindFlags = (BindFlags.ShaderResource | BindFlags.UnorderedAccess)
			});
			_yViews[i] = device.CreateUnorderedAccessView(_nv12Textures[i], new UnorderedAccessViewDescription
			{
				Format = Format.R8_UNorm,
				ViewDimension = UnorderedAccessViewDimension.Texture2D
			});
			_uvViews[i] = device.CreateUnorderedAccessView(_nv12Textures[i], new UnorderedAccessViewDescription
			{
				Format = Format.R8G8_UNorm,
				ViewDimension = UnorderedAccessViewDimension.Texture2D
			});
			MediaFactory.MFCreateVideoSampleFromSurface(null, out var sample).CheckError();
			using (IMFMediaBuffer iMFMediaBuffer = MediaFactory.MFCreateDXGISurfaceBuffer(typeof(ID3D11Texture2D).GUID, _nv12Textures[i], 0u, false))
			{
				iMFMediaBuffer.CurrentLength = iMFMediaBuffer.MaxLength;
				sample.AddBuffer(iMFMediaBuffer);
			}
			_samples[i] = sample;
			_slotBySamplePointer[sample.NativePointer] = i;
			_freeSlots.Enqueue(i);
		}
	}

	public static bool TryCreate(Screen screen, int width, int height, out GpuEncoderContext? gpuContext, out string failureReason)
	{
		gpuContext = null;
		if (width % 2 != 0 || height % 2 != 0)
		{
			failureReason = $"odd_capture_dimensions_{width}x{height}";
			return false;
		}
		ID3D11Device device = null;
		ID3D11DeviceContext immediateContext = null;
		ID3D11Multithread iD3D11Multithread = null;
		IMFDXGIDeviceManager iMFDXGIDeviceManager = null;
		ID3D11ComputeShader iD3D11ComputeShader = null;
		ID3D11ComputeShader iD3D11ComputeShader2 = null;
		ID3D11SamplerState iD3D11SamplerState = null;
		MediaFoundationRuntime.Acquire();
		try
		{
			using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
			using IDXGIAdapter1 iDXGIAdapter = FindAdapterForScreen(factory, screen);
			if (iDXGIAdapter == null)
			{
				failureReason = "no_adapter_for_display_" + screen.DeviceName;
				return false;
			}
			FeatureLevel[] featureLevels = new FeatureLevel[4]
			{
				FeatureLevel.Level_12_1,
				FeatureLevel.Level_12_0,
				FeatureLevel.Level_11_1,
				FeatureLevel.Level_11_0
			};
			Result result = D3D11.D3D11CreateDevice(iDXGIAdapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport, featureLevels, out device, out FeatureLevel _, out immediateContext);
			if (result.Failure || device == null || immediateContext == null)
			{
				failureReason = $"video_support_device_failed_0x{result.Code:x8}";
				return false;
			}
			iD3D11Multithread = immediateContext.QueryInterface<ID3D11Multithread>();
			iD3D11Multithread.SetMultithreadProtected(true);
			FormatSupport formatSupport = device.CheckFormatSupport(Format.NV12);
			if ((formatSupport & (FormatSupport.Texture2D | FormatSupport.TypedUnorderedAccessView)) != (FormatSupport.Texture2D | FormatSupport.TypedUnorderedAccessView))
			{
				failureReason = $"nv12_uav_unsupported_{(int)formatSupport:x8}";
				return false;
			}
			AdapterDescription1 description = iDXGIAdapter.Description1;
			Luid luid = description.Luid;
			byte[] array = new byte[8];
			BitConverter.GetBytes(luid.LowPart).CopyTo(array, 0);
			BitConverter.GetBytes(luid.HighPart).CopyTo(array, 4);
			string text = FindHardwareH264Mft(array);
			if (text == null)
			{
				failureReason = "no_hardware_h264_mft_on_adapter";
				return false;
			}
			iD3D11ComputeShader = CompileShader(device, "\r\nTexture2D<float3> Input : register(t0);\r\nRWTexture2D<unorm float>  OutY  : register(u0);\r\nRWTexture2D<unorm float2> OutUV : register(u1);\r\nSamplerState LinearSampler : register(s0);\r\n\r\n// BT.709 RGB->YUV rows\r\nstatic const float3 YR = {  0.2126,  0.7152,  0.0722 };\r\nstatic const float3 UR = { -0.1146, -0.3854,  0.5    };\r\nstatic const float3 VR = {  0.5,    -0.4542, -0.0458 };\r\n// limited (video) range\r\nstatic const float RANGE_Y   = 219.0 / 255.0;\r\nstatic const float OFFSET_Y  = 16.0 / 255.0;\r\nstatic const float RANGE_UV  = 224.0 / 255.0;\r\nstatic const float OFFSET_UV = 0.5 / 255.0 + 0.5;\r\n\r\n[numthreads(16, 16, 1)]\r\nvoid main(uint3 P : SV_DispatchThreadID)\r\n{\r\n    uint4 Pos4 = P.xyxy * 2 + uint4(0, 0, 1, 1);\r\n\r\n    float2 InSize;\r\n    Input.GetDimensions(InSize.x, InSize.y);\r\n\r\n    float2 ColorPos = float2(Pos4.xy) / InSize;\r\n    float3 C0 = Input.SampleLevel(LinearSampler, ColorPos, 0, int2(0, 1));\r\n    float3 C1 = Input.SampleLevel(LinearSampler, ColorPos, 0, int2(1, 1));\r\n    float3 C = lerp(C0, C1, 0.5);\r\n\r\n    OutUV[P.xy] = float2(dot(UR, C), dot(VR, C)) * RANGE_UV + OFFSET_UV;\r\n\r\n    OutY[Pos4.xy] = dot(YR, Input[Pos4.xy]) * RANGE_Y + OFFSET_Y;\r\n    OutY[Pos4.zy] = dot(YR, Input[Pos4.zy]) * RANGE_Y + OFFSET_Y;\r\n    OutY[Pos4.xw] = dot(YR, Input[Pos4.xw]) * RANGE_Y + OFFSET_Y;\r\n    OutY[Pos4.zw] = dot(YR, Input[Pos4.zw]) * RANGE_Y + OFFSET_Y;\r\n}\r\n", "gen2-convert");
			iD3D11ComputeShader2 = CompileShader(device, "\r\nTexture2D<float4> Cursor : register(t0);\r\nRWTexture2D<uint> Mailbox : register(u0);\r\n\r\ncbuffer CursorPlacement : register(b0)\r\n{\r\n    int2 TopLeft;      // cursor rect origin in mailbox pixels\r\n    int2 CursorSize;   // cursor sprite dimensions\r\n    int2 MailboxSize;  // mailbox dimensions (clip guard)\r\n    int2 _pad;\r\n};\r\n\r\nstatic uint PackToBGRA(float3 Color)\r\n{\r\n    const uint3 Pack = { 1 << 16, 1 << 8, 1 };\r\n    return dot(uint3(saturate(Color.rgb) * 255 + 0.5), Pack) | 0xff000000;\r\n}\r\n\r\nstatic float3 UnpackBGRA(uint Value)\r\n{\r\n    return float3((Value >> 16) & 0xff, (Value >> 8) & 0xff, Value & 0xff) / 255.0;\r\n}\r\n\r\n[numthreads(8, 8, 1)]\r\nvoid main(uint3 P : SV_DispatchThreadID)\r\n{\r\n    if ((int)P.x >= CursorSize.x || (int)P.y >= CursorSize.y)\r\n    {\r\n        return;\r\n    }\r\n\r\n    int2 Dest = TopLeft + int2(P.xy);\r\n    if (Dest.x < 0 || Dest.y < 0 || Dest.x >= MailboxSize.x || Dest.y >= MailboxSize.y)\r\n    {\r\n        return;\r\n    }\r\n\r\n    float4 Src = Cursor[P.xy]; // straight-alpha BGRA sprite (DrawIconEx output)\r\n    if (Src.a <= 0.0)\r\n    {\r\n        return;\r\n    }\r\n\r\n    float3 Background = UnpackBGRA(Mailbox[uint2(Dest)]);\r\n    float3 Blended = lerp(Background, Src.rgb, Src.a);\r\n    Mailbox[uint2(Dest)] = PackToBGRA(Blended);\r\n}\r\n", "gen2-cursor");
			iD3D11SamplerState = device.CreateSamplerState(new SamplerDescription
			{
				Filter = Filter.MinMagMipLinear,
				AddressU = TextureAddressMode.Clamp,
				AddressV = TextureAddressMode.Clamp,
				AddressW = TextureAddressMode.Clamp
			});
			iMFDXGIDeviceManager = MediaFactory.MFCreateDXGIDeviceManager();
			iMFDXGIDeviceManager.ResetDevice(device);
			gpuContext = new GpuEncoderContext(device, immediateContext, iD3D11Multithread, iMFDXGIDeviceManager, iD3D11ComputeShader, iD3D11ComputeShader2, iD3D11SamplerState, width, height, text, description.Description, ((long)luid.HighPart << 32) | luid.LowPart);
			failureReason = "";
			return true;
		}
		catch (Exception ex)
		{
			failureReason = "gen2_bring_up_" + ex.GetType().Name + "_" + ex.Message;
			return false;
		}
		finally
		{
			if (gpuContext == null)
			{
				iD3D11SamplerState?.Dispose();
				iD3D11ComputeShader2?.Dispose();
				iD3D11ComputeShader?.Dispose();
				iMFDXGIDeviceManager?.Dispose();
				iD3D11Multithread?.Dispose();
				immediateContext?.Dispose();
				device?.Dispose();
				MediaFoundationRuntime.Release();
			}
		}
	}

	private static IDXGIAdapter1? FindAdapterForScreen(IDXGIFactory1 factory, Screen screen)
	{
		long? displayConfigLuid = DxgiDisplayResolver.TryGetDisplayConfigAdapterLuid(screen);
		uint? num = null;
		int num2 = 0;
		IDXGIAdapter1 adapterOut;
		for (uint num3 = 0u; factory.EnumAdapters1(num3, out adapterOut).Success; num3++)
		{
			AdapterDescription1 description = adapterOut.Description1;
			long adapterLuid = DxgiDisplayResolver.AdapterLuidToInt64(description.Luid.LowPart, description.Luid.HighPart);
			IDXGIOutput outputOut;
			for (uint num4 = 0u; adapterOut.EnumOutputs(num4, out outputOut).Success; num4++)
			{
				using (outputOut)
				{
					OutputDescription description2 = outputOut.Description;
					if ((bool)description2.AttachedToDesktop)
					{
						DxgiDisplayMatch dxgiDisplayMatch = DxgiDisplayResolver.Score(screen, description2, adapterLuid, displayConfigLuid);
						if (dxgiDisplayMatch.Score > num2)
						{
							num = num3;
							num2 = dxgiDisplayMatch.Score;
						}
					}
				}
			}
			adapterOut.Dispose();
		}
		if (num.HasValue)
		{
			factory.EnumAdapters1(num.Value, out var adapterOut2).CheckError();
			return adapterOut2;
		}
		return null;
	}

	private static string? FindHardwareH264Mft(byte[] luidBytes)
	{
		using IMFAttributes iMFAttributes = MediaFactory.MFCreateAttributes(1u);
		iMFAttributes.SetBlob(MftEnumAdapterLuid, luidBytes);
		RegisterTypeInfo value = new RegisterTypeInfo
		{
			GuidMajorType = MediaTypeGuids.Video,
			GuidSubtype = VideoFormatGuids.NV12
		};
		RegisterTypeInfo value2 = new RegisterTypeInfo
		{
			GuidMajorType = MediaTypeGuids.Video,
			GuidSubtype = VideoFormatGuids.H264
		};
		MediaFactory.MFTEnum2(TransformCategoryGuids.VideoEncoder, 70u, value, value2, iMFAttributes, out var pppMFTActivate, out var pnumMFTActivate);
		if (pnumMFTActivate == 0 || pppMFTActivate == IntPtr.Zero)
		{
			if (pppMFTActivate != IntPtr.Zero)
			{
				Marshal.FreeCoTaskMem(pppMFTActivate);
			}
			return null;
		}
		string text = null;
		for (int i = 0; i < pnumMFTActivate; i++)
		{
			nint num = Marshal.ReadIntPtr(pppMFTActivate, i * IntPtr.Size);
			if (text == null)
			{
				using IMFActivate iMFActivate = new IMFActivate(num);
				try
				{
					text = iMFActivate.GetString(TransformAttributeKeys.MftFriendlyNameAttribute);
				}
				catch
				{
					text = "hardware_h264_mft_unnamed";
				}
			}
			else
			{
				Marshal.Release(num);
			}
		}
		Marshal.FreeCoTaskMem(pppMFTActivate);
		return text;
	}

	private static ID3D11ComputeShader CompileShader(ID3D11Device device, string source, string name)
	{
		return device.CreateComputeShader(Compiler.Compile(source, "main", name, "cs_5_0").Span);
	}

	public int AcquireSlot(TimeSpan timeout)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (!_poolGate.Wait(timeout))
		{
			return -1;
		}
		if (!_freeSlots.TryDequeue(out var result))
		{
			_poolGate.Release();
			throw new InvalidOperationException("Gen2 pool gate/queue disagreement.");
		}
		Volatile.Write(ref _slotReturnableByCaller[result], 1);
		return result;
	}

	public void ReturnUnsubmittedSlot(int slot)
	{
		if (Interlocked.Exchange(ref _slotReturnableByCaller[slot], 0) == 1)
		{
			_freeSlots.Enqueue(slot);
			_poolGate.Release();
		}
	}

	public void ConvertToSlot(ID3D11ShaderResourceView mailboxView, int slot)
	{
		_context.ClearState();
		_context.CSSetShader(_convertShader);
		_context.CSSetShaderResource(0u, mailboxView);
		_context.CSSetSampler(0u, _linearSampler);
		_context.CSSetUnorderedAccessView(0u, _yViews[slot]);
		_context.CSSetUnorderedAccessView(1u, _uvViews[slot]);
		_context.Dispatch((uint)((Width / 2 + 15) / 16), (uint)((Height / 2 + 15) / 16), 1u);
		_context.ClearState();
		_context.Flush();
	}

	public void WriteSampleToWriter(IMFSinkWriter writer, int streamIndex, int slot, long ptsHns, long durationHns)
	{
		IMFSample iMFSample = _samples[slot];
		iMFSample.SampleTime = ptsHns;
		iMFSample.SampleDuration = durationHns;
		using (IMFTrackedSample iMFTrackedSample = iMFSample.QueryInterface<IMFTrackedSample>())
		{
			iMFTrackedSample.SetAllocator(_releaseCallback, null);
		}
		Volatile.Write(ref _slotReturnableByCaller[slot], 0);
		try
		{
			writer.WriteSample(streamIndex, iMFSample);
		}
		finally
		{
			iMFSample.Release();
		}
	}

	public IMFAttributes CreateSinkWriterAttributes()
	{
		Guid guidKey = new Guid("ec822da2-e1e9-4b29-a0d8-563c719f5269");
		IMFAttributes iMFAttributes = MediaFactory.MFCreateAttributes(4u);
		iMFAttributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1u);
		iMFAttributes.Set(guidKey, _deviceManager);
		iMFAttributes.Set(TranscodeAttributeKeys.TranscodeContainertype, TranscodeContainerTypeGuids.Mpeg4);
		return iMFAttributes;
	}

	private void OnSampleReleased(nint samplePointer)
	{
		if (samplePointer != IntPtr.Zero && _slotBySamplePointer.TryGetValue(samplePointer, out var value))
		{
			_freeSlots.Enqueue(value);
			try
			{
				_poolGate.Release();
				return;
			}
			catch (SemaphoreFullException)
			{
				KuroLog.Capture("gen2 pool release overflow — accounting bug, slot dropped", "OnSampleReleased", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\GpuEncoderContext.cs", 512);
				return;
			}
		}
		KuroLog.Capture($"gen2 pool slot lost: sample callback pointer={samplePointer:x} unmapped", "OnSampleReleased", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\GpuEncoderContext.cs", 519);
		if (samplePointer != IntPtr.Zero)
		{
			Marshal.Release(samplePointer);
		}
	}

	public void AddUser()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		Interlocked.Increment(ref _userCount);
	}

	public void ReleaseUser()
	{
		Dispose();
	}

	public void Dispose()
	{
		if (Interlocked.Decrement(ref _userCount) > 0 || _disposed)
		{
			return;
		}
		_disposed = true;
		int num = 0;
		for (int i = 0; i < 8; i++)
		{
			if (_poolGate.Wait(TimeSpan.FromMilliseconds(500.0)))
			{
				num++;
			}
		}
		HashSet<int> hashSet = new HashSet<int>();
		int result;
		while (_freeSlots.TryDequeue(out result))
		{
			hashSet.Add(result);
		}
		if (hashSet.Count < 8)
		{
			KuroLog.Capture($"gen2 chain dispose leaking undrained pool slots drained={num} free={hashSet.Count}/{8}", "Dispose", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\GpuEncoderContext.cs", 570);
		}
		for (int j = 0; j < 8; j++)
		{
			if (hashSet.Contains(j))
			{
				_samples[j]?.Dispose();
			}
			_yViews[j]?.Dispose();
			_uvViews[j]?.Dispose();
			_nv12Textures[j]?.Dispose();
		}
		_linearSampler.Dispose();
		_cursorShader.Dispose();
		_convertShader.Dispose();
		_deviceManager.Dispose();
		_multithread.Dispose();
		_context.Dispose();
		_device.Dispose();
		MediaFoundationRuntime.Release();
	}
}
