using FluentAssertions;
using SamedisCare.SplSync.Core.Config;
using Xunit;

namespace SamedisCare.SplSync.Core.Tests;

public class ConfigStoreTests
{
    [Fact]
    public void Loads_minimal_yaml()
    {
        var yaml = @"
auth:
  uri: ""https://ident.services""
  client_id: ""test@example.com""
  client_secret: ""s3cret""
samedis:
  uri: ""https://sync.samedis.care""
  api_version: ""v4""
actimed:
  database_path: ""C:/x/actimed3db.mdb""
  protocol_pdf_dir: """"
  use_local_snapshot: true
  default_tester_name: ""Tester""
branding:
  service_provider_name: ""Test GmbH""
  logo_path: """"
tenants:
  - name: ""Mandant A""
    samedis_tenant_id: ""63f5c0491b57cc000df2b2c7""
    actimed_cust_ids: [4, 7]
    enabled: true
sync:
  download_interval_minutes: 5
  upload_poll_interval_seconds: 30
  download_inventories: true
  download_open_issues: true
  upload_finished_issues: true
  upload_mode_pdf_pickup: false
  upload_mode_png_on_completion: true
maintenance_kind_mapping:
  - match: ""(?i)dguv\\s*v?3""
    actimed_kind: ""MPBe_§11_STK/DGUV V3""
  - actimed_kind: ""MPBe_§7_Wartung/Inspektion""
logging:
  level: 1
  mode: 3
http:
  valid_certificate: true
";
        var path = Path.GetTempFileName();
        File.WriteAllText(path, yaml);
        try
        {
            var cfg = ConfigStore.Load(path);
            cfg.Auth.ClientId.Should().Be("test@example.com");
            cfg.Tenants.Should().HaveCount(1);
            cfg.Tenants[0].ActimedCustIds.Should().BeEquivalentTo(new[] { 4, 7 });
            cfg.Sync.DownloadIntervalMinutes.Should().Be(5);
            cfg.Sync.UploadPollIntervalSeconds.Should().Be(30);
            cfg.Sync.UploadModePngOnCompletion.Should().BeTrue();
            cfg.Sync.UploadModePdfPickup.Should().BeFalse();
            cfg.MaintenanceKindMapping.Should().HaveCount(2);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
