using SamedisCare.SplSync.Core.Api;
using Newtonsoft.Json;

using SamedisCare.Api.Common;
using SamedisCare.Api.Http;
using SamedisCare.Helper.Logging;

namespace SamedisCare.SplSync.Core.Api;

/// <summary>
/// JSON:API model for /api/{version}/{tenant_scope}/inventories.
/// Slimmed to the fields we actually need for the Actimed write side (5.2):
/// device_number, serial_number, device_model_*, device_type_title, manufacturer.
/// Adapted from `Inventories.cs` in the reference repo.
/// </summary>
public class Inventories
{
    public class Attributes
    {
        [JsonProperty("id")] public string? Id { get; set; }
        [JsonProperty("tenant_id")] public string? TenantId { get; set; }
        [JsonProperty("device_number")] public string? DeviceNumber { get; set; }
        [JsonProperty("inventory_number")] public string? InventoryNumber { get; set; }
        [JsonProperty("serial_number")] public string? SerialNumber { get; set; }

        [JsonProperty("device_model_title")] public string? DeviceModelTitle { get; set; }
        [JsonProperty("device_model_version")] public string? DeviceModelVersion { get; set; }
        [JsonProperty("device_model_current_responsible_manufacturer")] public string? DeviceModelCurrentResponsibleManufacturer { get; set; }
        [JsonProperty("device_model_manufacturer_according_to_type_plate")] public string? DeviceModelManufacturerAccordingToTypePlate { get; set; }

        [JsonProperty("device_type_title")] public string? DeviceTypeTitle { get; set; }

        [JsonProperty("device_location_id")] public string? DeviceLocationId { get; set; }
        [JsonProperty("device_location_title")] public string? DeviceLocationTitle { get; set; }
        [JsonProperty("source_location_id")] public string? SourceLocationId { get; set; }

        [JsonProperty("department_id")] public string? DepartmentId { get; set; }
        [JsonProperty("department_title")] public string? DepartmentTitle { get; set; }

        [JsonProperty("operation_status")] public string? OperationStatus { get; set; }
        [JsonProperty("no_medical_device")] public bool? NoMedicalDevice { get; set; }
        [JsonProperty("do_maintenance")] public bool? DoMaintenance { get; set; }

        [JsonProperty("created_at")] public string? CreatedAt { get; set; }
        [JsonProperty("updated_at")] public string? UpdatedAt { get; set; }
    }

    public class Data
    {
        [JsonProperty("id")] public string? Id { get; set; }
        [JsonProperty("type")] public string? Type { get; set; }
        [JsonProperty("attributes")] public Attributes? Attributes { get; set; }
    }

    public class Meta
    {
        [JsonProperty("total")] public int? Total { get; set; }
    }

    public class Root
    {
        [JsonProperty("data")]
        [JsonConverter(typeof(JsonApi.SingleOrArrayConverter<Data>))]
        public List<Data>? Data { get; set; }

        [JsonProperty("meta")] public Meta? Meta { get; set; }
    }
}
