using RgbBlaster.Core;
using Xunit;

namespace RgbBlaster.Tests;

public class VendorAppsTests
{
    [Fact]
    public void FindsEachAppOnceIgnoringCase()
    {
        var found = VendorApps.Find(["explorer", "icue", "iCUEDevicePluginHost", "Redragon K580RGB", "svchost"]);
        Assert.Equal(["Corsair iCUE", "Redragon K580 software"], found.Select(a => a.Name));
    }

    [Fact]
    public void NothingFoundWithoutVendorApps()
    {
        Assert.Empty(VendorApps.Find(["explorer", "svchost", "logi_lamparray_service.AMD64", "rgbblaster-service"]));
    }
}
