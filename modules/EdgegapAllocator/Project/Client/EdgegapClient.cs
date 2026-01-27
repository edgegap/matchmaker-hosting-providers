using System.Net.Http;

namespace EdgegapAllocator.Client;

public interface IEdgegapHttpClientFactory
{
	HttpClient CreateClient(string apiToken);
}

public class EdgegapHttpClientFactory : IEdgegapHttpClientFactory
{
	public HttpClient CreateClient(string apiToken)
	{
		var client = new HttpClient();
		client.DefaultRequestHeaders.Add("Authorization", $"{apiToken}");
		return client;
	}
}