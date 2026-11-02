using System;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace KuroWindows.RecorderCore.Encode;

internal static class H264EncoderConfig
{
	[StructLayout(LayoutKind.Explicit, Size = 24)]
	internal struct Variant
	{
		[FieldOffset(0)]
		public ushort Vt;

		[FieldOffset(8)]
		public uint UlVal;

		[FieldOffset(8)]
		public short BoolVal;

		public static Variant UI4(uint value)
		{
			return new Variant
			{
				Vt = 19,
				UlVal = value
			};
		}

		public static Variant Bool(bool value)
		{
			return new Variant
			{
				Vt = 11,
				BoolVal = (short)(value ? (-1) : 0)
			};
		}
	}

	private struct PropertyKey
	{
		public Guid Fmtid;

		public uint Pid;
	}

	internal static readonly Guid H264Subtype = new Guid("34363248-0000-0010-8000-00aa00389b71");

	private static readonly Guid CodecApiRateControlMode = new Guid("1c0608e9-370c-4710-8a58-cb6181c42423");

	private static readonly Guid CodecApiMeanBitRate = new Guid("f7222374-2144-4815-b550-a37f8e12ee52");

	private static readonly Guid CodecApiBPictureCount = new Guid("8d390aac-dc5c-4200-b57f-814d04babab2");

	private static readonly Guid CodecApiCabacEnable = new Guid("ee6cad62-d305-4248-a50e-e1b255f7caf8");

	private static readonly Guid CodecApiIid = new Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");

	private const uint EAVEncH264VProfileHigh = 100u;

	private static readonly Guid MfXvpDisableFrc = new Guid(738916889, 31383, 19802, 158, 232, 22, 212, 252, 81, 141, 140);

	private static readonly Guid IidIPropertyStore = new Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");

	public static void SetUInt32(IMFAttributes attributes, Guid key, uint value)
	{
		attributes.Set(key, value);
	}

	public static IMFMediaType CreateOutputMediaType(int width, int height, int framesPerSecond, uint avgBitrateBps, H264WireProfile profile)
	{
		IMFMediaType iMFMediaType = MediaFactory.MFCreateMediaType();
		iMFMediaType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
		iMFMediaType.Set(MediaTypeAttributeKeys.Subtype, H264Subtype);
		if (profile == H264WireProfile.High)
		{
			SetUInt32(iMFMediaType, MediaTypeAttributeKeys.Mpeg2Profile, 100u);
		}
		SetUInt32(iMFMediaType, MediaTypeAttributeKeys.AvgBitrate, avgBitrateBps);
		SetUInt32(iMFMediaType, MediaTypeAttributeKeys.InterlaceMode, 2u);
		iMFMediaType.Set(MediaTypeAttributeKeys.FrameSize, Pack(width, height));
		iMFMediaType.Set(MediaTypeAttributeKeys.FrameRate, Pack(framesPerSecond, 1));
		iMFMediaType.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
		if (profile == H264WireProfile.Legacy)
		{
			SetUInt32(iMFMediaType, MediaTypeAttributeKeys.AllSamplesIndependent, 1u);
		}
		return iMFMediaType;
	}

	public unsafe static H264EncoderProperties ConfigureEncoderProperties(IMFSinkWriter writer, int streamIndex, uint meanBitrateBps, bool banBFrames, bool enableCabac)
	{
		nint num = IntPtr.Zero;
		int? rcModeHr = null;
		int? meanBitRateHr = null;
		int? bFramesHr = null;
		int? cabacHr = null;
		try
		{
			num = writer.GetServiceForStream(streamIndex, Guid.Empty, CodecApiIid);
			delegate* unmanaged[Stdcall]<nint, Guid*, Variant*, int> obj = (delegate* unmanaged[Stdcall]<nint, Guid*, Variant*, int>)(*(IntPtr*)((nint)(*(IntPtr*)num) + (nint)9 * (nint)sizeof(void*)));
			Guid codecApiRateControlMode = CodecApiRateControlMode;
			Variant variant = Variant.UI4(2u);
			rcModeHr = obj(num, &codecApiRateControlMode, &variant);
			Guid codecApiMeanBitRate = CodecApiMeanBitRate;
			Variant variant2 = Variant.UI4(meanBitrateBps);
			meanBitRateHr = obj(num, &codecApiMeanBitRate, &variant2);
			if (banBFrames)
			{
				Guid codecApiBPictureCount = CodecApiBPictureCount;
				Variant variant3 = Variant.UI4(0u);
				bFramesHr = obj(num, &codecApiBPictureCount, &variant3);
			}
			if (enableCabac)
			{
				Guid codecApiCabacEnable = CodecApiCabacEnable;
				Variant variant4 = Variant.Bool(value: true);
				cabacHr = obj(num, &codecApiCabacEnable, &variant4);
			}
			return new H264EncoderProperties(rcModeHr, meanBitRateHr, bFramesHr, cabacHr, null);
		}
		catch (Exception ex)
		{
			return new H264EncoderProperties(rcModeHr, meanBitRateHr, bFramesHr, cabacHr, ex.Message);
		}
		finally
		{
			if (num != IntPtr.Zero)
			{
				Marshal.Release(num);
			}
		}
	}

	public static string? DisableFrameRateConversion(IMFSinkWriter writer, int streamIndex)
	{
		try
		{
			using IMFSinkWriterEx iMFSinkWriterEx = writer.QueryInterface<IMFSinkWriterEx>();
			int num = 0;
			while (true)
			{
				IMFTransform transform;
				try
				{
					iMFSinkWriterEx.GetTransformForStream(streamIndex, num, out var _, out transform);
				}
				catch (SharpGenException)
				{
					break;
				}
				using (transform)
				{
					transform.Attributes?.Set(MfXvpDisableFrc, 1u);
				}
				num++;
			}
			return null;
		}
		catch (Exception ex2)
		{
			return ex2.Message;
		}
	}

	public static H264EncoderChainInfo TryReadEncoderInfo(IMFSinkWriter writer, int streamIndex)
	{
		int num = 0;
		string encoderMftName = null;
		bool isHardwareMft = false;
		uint? negotiatedProfile = null;
		uint? negotiatedLevel = null;
		try
		{
			using IMFSinkWriterEx iMFSinkWriterEx = writer.QueryInterface<IMFSinkWriterEx>();
			int num2 = 0;
			while (true)
			{
				Guid guidCategory;
				IMFTransform transform;
				try
				{
					iMFSinkWriterEx.GetTransformForStream(streamIndex, num2, out guidCategory, out transform);
				}
				catch (SharpGenException)
				{
					break;
				}
				using (transform)
				{
					num++;
					if (!(guidCategory != TransformCategoryGuids.VideoEncoder))
					{
						try
						{
							IMFAttributes attributes = transform.Attributes;
							if ((object)attributes != null)
							{
								try
								{
									encoderMftName = attributes.GetString(TransformAttributeKeys.MftFriendlyNameAttribute);
								}
								catch
								{
								}
								try
								{
									isHardwareMft = !string.IsNullOrEmpty(attributes.GetString(TransformAttributeKeys.MftEnumHardwareUrlAttribute));
								}
								catch
								{
									isHardwareMft = false;
								}
							}
						}
						catch
						{
						}
						try
						{
							using IMFMediaType iMFMediaType = transform.GetOutputCurrentType(0);
							if (iMFMediaType.GetUInt32(MediaTypeAttributeKeys.Mpeg2Profile, out var punValue).Success)
							{
								negotiatedProfile = punValue;
							}
							if (iMFMediaType.GetUInt32(MediaTypeAttributeKeys.Mpeg2Level, out var punValue2).Success)
							{
								negotiatedLevel = punValue2;
							}
						}
						catch
						{
						}
					}
				}
				num2++;
			}
		}
		catch
		{
		}
		return new H264EncoderChainInfo(num, encoderMftName, isHardwareMft, negotiatedProfile, negotiatedLevel);
	}

	public static string FormatHr(int? hr)
	{
		if (hr.HasValue)
		{
			int valueOrDefault = hr.GetValueOrDefault();
			return $"0x{valueOrDefault:x8}";
		}
		return "-";
	}

	private static ulong Pack(int high, int low)
	{
		return ((ulong)(uint)high << 32) | (uint)low;
	}

	[DllImport("propsys.dll", ExactSpelling = true)]
	private static extern int PSCreateMemoryPropertyStore(ref Guid riid, out nint propertyStore);

	public unsafe static nint CreateHighProfileEncoderConfigStore(out string? error)
	{
		error = null;
		Guid riid = IidIPropertyStore;
		int num;
		nint propertyStore;
		try
		{
			num = PSCreateMemoryPropertyStore(ref riid, out propertyStore);
		}
		catch (Exception ex)
		{
			error = ex.Message;
			return IntPtr.Zero;
		}
		if (num < 0 || propertyStore == IntPtr.Zero)
		{
			error = $"pscreatememorypropertystore_hr=0x{num:x8}";
			return IntPtr.Zero;
		}
		try
		{
			delegate* unmanaged[Stdcall]<nint, PropertyKey*, Variant*, int> obj = (delegate* unmanaged[Stdcall]<nint, PropertyKey*, Variant*, int>)(*(IntPtr*)((nint)(*(IntPtr*)propertyStore) + (nint)6 * (nint)sizeof(void*)));
			int num2 = 0;
			int num3 = 0;
			Span<uint> span = stackalloc uint[2] { 0u, 2u };
			for (int i = 0; i < span.Length; i++)
			{
				uint pid = span[i];
				PropertyKey propertyKey = new PropertyKey
				{
					Fmtid = CodecApiBPictureCount,
					Pid = pid
				};
				Variant variant = Variant.UI4(0u);
				int num4 = obj(propertyStore, &propertyKey, &variant);
				if (num4 < 0)
				{
					num2 = num4;
				}
				PropertyKey propertyKey2 = new PropertyKey
				{
					Fmtid = CodecApiCabacEnable,
					Pid = pid
				};
				Variant variant2 = Variant.Bool(value: true);
				int num5 = obj(propertyStore, &propertyKey2, &variant2);
				if (num5 < 0)
				{
					num3 = num5;
				}
			}
			if (num2 < 0 || num3 < 0)
			{
				error = $"store_setvalue b_hr=0x{num2:x8} cabac_hr=0x{num3:x8}";
				Marshal.Release(propertyStore);
				return IntPtr.Zero;
			}
			return propertyStore;
		}
		catch (Exception ex2)
		{
			error = ex2.Message;
			Marshal.Release(propertyStore);
			return IntPtr.Zero;
		}
	}

	public static IMFAttributes CreateHighProfileEncodingParameters()
	{
		IMFAttributes iMFAttributes = MediaFactory.MFCreateAttributes(2u);
		SetUInt32(iMFAttributes, CodecApiBPictureCount, 0u);
		SetUInt32(iMFAttributes, CodecApiCabacEnable, 1u);
		return iMFAttributes;
	}

	public unsafe static (uint? BFrameCount, bool? CabacEnabled) TryReadBackEncoderProperties(IMFSinkWriter writer, int streamIndex)
	{
		nint num = IntPtr.Zero;
		try
		{
			num = writer.GetServiceForStream(streamIndex, Guid.Empty, CodecApiIid);
			delegate* unmanaged[Stdcall]<nint, Guid*, Variant*, int> obj = (delegate* unmanaged[Stdcall]<nint, Guid*, Variant*, int>)(*(IntPtr*)((nint)(*(IntPtr*)num) + (nint)8 * (nint)sizeof(void*)));
			uint? item = null;
			bool? item2 = null;
			Guid codecApiBPictureCount = CodecApiBPictureCount;
			Variant variant = default;
			if (obj(num, &codecApiBPictureCount, &variant) >= 0 && variant.Vt == 19)
			{
				item = variant.UlVal;
			}
			Guid codecApiCabacEnable = CodecApiCabacEnable;
			variant = default;
			if (obj(num, &codecApiCabacEnable, &variant) >= 0 && variant.Vt == 11)
			{
				item2 = variant.BoolVal != 0;
			}
			return (BFrameCount: item, CabacEnabled: item2);
		}
		catch
		{
			return (BFrameCount: null, CabacEnabled: null);
		}
		finally
		{
			if (num != IntPtr.Zero)
			{
				Marshal.Release(num);
			}
		}
	}
}
