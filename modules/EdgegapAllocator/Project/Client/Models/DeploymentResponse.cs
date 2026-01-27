using Newtonsoft.Json;

namespace EdgegapAllocator.Client.Models;

public class DeploymentResponse
{
	[JsonProperty("request_id")]
	public required string RequestId { get; set; }
}