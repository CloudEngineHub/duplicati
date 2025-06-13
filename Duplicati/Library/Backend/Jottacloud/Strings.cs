// Copyright (C) 2025, The Duplicati Team
// https://duplicati.com, hello@duplicati.com
// 
// Permission is hereby granted, free of charge, to any person obtaining a 
// copy of this software and associated documentation files (the "Software"), 
// to deal in the Software without restriction, including without limitation 
// the rights to use, copy, modify, merge, publish, distribute, sublicense, 
// and/or sell copies of the Software, and to permit persons to whom the 
// Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in 
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS 
// OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, 
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE 
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER 
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING 
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER 
// DEALINGS IN THE SOFTWARE.

using Duplicati.Library.Localization.Short;

namespace Duplicati.Library.Backend.Strings;

internal static class Jottacloud
{
    public static string Description => LC.L(@"This backend can read and write data to Jottacloud using its REST protocol. Allowed format is ""jottacloud://folder/subfolder"".");
    public static string DisplayName => LC.L(@"Jottacloud");
    public static string NoUsernameError => LC.L(@"No username found");
    public static string NoPathError => LC.L(@"No path given. Files cannot be uploaded to the root folder");
    public static string IllegalMountPoint => LC.L(@"Illegal mount point given.");
    public static string FileUploadError => LC.L(@"Failed to upload file");
    public static string DescriptionDeviceLong(string mountPointOption) { return LC.L(@"The backup device to use. Will be created if not already exists. You can manage your devices from the backup panel in the Jottacloud web interface. When you specify a custom device you should also specify the mount point to use on this device with the ""{0}"" option.", mountPointOption); }
    public static string DescriptionDeviceShort => LC.L(@"Supply the backup device to use");
    public static string DescriptionMountPointLong(string deviceOptionName) { return LC.L(@"The mount point to use on the server. The default is ""Archive"" for using the built-in archive mount point. Set this option to ""Sync"" to use the built-in synchronization mount point instead, or if you have specified a custom device with option ""{0}"" you are free to name the mount point as you like.", deviceOptionName); }
    public static string DescriptionMountPointShort => LC.L(@"Supply the mount point to use on the server");
    public static string ThreadsLong => LC.L(@"Number of threads for restore operations. In some cases the download rate is limited to 18.5 Mbps per stream. Use multiple threads to increase throughput.");
    public static string ThreadsShort => LC.L(@"Number of threads for restore operations");
    public static string ChunksizeLong => LC.L(@"The chunk size for simultaneous downloading. These chunks will be held in memory, so keep it as low as possible.");
    public static string ChunksizeShort => LC.L(@"The chunk size for simultaneous downloading");
}