using System.Management;

namespace DeviceIdentity;

internal static class Wmi
{
	internal static string? FirstValue(string className, string propertyName)
	{
		try
		{
			using ManagementObjectSearcher managementObjectSearcher = new ManagementObjectSearcher("SELECT " + propertyName + " FROM " + className);
			foreach (ManagementObject item in managementObjectSearcher.Get())
			{
				using (item)
				{
					string text = item[propertyName]?.ToString();
					if (!string.IsNullOrWhiteSpace(text))
					{
						return text;
					}
				}
			}
		}
		catch
		{
		}
		return null;
	}
}
