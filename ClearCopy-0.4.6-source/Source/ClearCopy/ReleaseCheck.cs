using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ClearCopy;

internal static class ReleaseCheck
{
	internal const string ReleasesPage = "https://github.com/CS-Colin/ClearCopy/releases";
	private const string LatestReleaseApi = "https://api.github.com/repos/CS-Colin/ClearCopy/releases/latest";

	internal static Task<Version> LatestVersionAsync(Version current)
	{
		return Task.Run(() =>
		{
			var request = (HttpWebRequest)WebRequest.Create(LatestReleaseApi);
			request.Accept = "application/vnd.github+json";
			request.UserAgent = "ClearCopy/" + current;
			request.Timeout = 10000;
			request.ReadWriteTimeout = 10000;
			request.AllowAutoRedirect = false;
			using (var response = request.GetResponse())
			using (var reader = new StreamReader(response.GetResponseStream()))
			{
				var release = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
				if (release == null || !release.TryGetValue("tag_name", out object value) ||
					!(value is string tag) || !Version.TryParse(tag.TrimStart('v', 'V'), out Version version))
				{
					throw new InvalidDataException("The latest release has an invalid version tag.");
				}
				return SelfInstall.Normalize(version);
			}
		});
	}
}
