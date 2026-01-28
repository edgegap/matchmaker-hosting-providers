using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using EdgegapAllocator.Client;
using EdgegapAllocator.Client.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Unity.Services.CloudCode.Apis;
using Unity.Services.CloudCode.Apis.Matchmaker;
using Unity.Services.CloudCode.Core;
using IExecutionContext = Unity.Services.CloudCode.Core.IExecutionContext;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace EdgegapAllocator;

/// <summary>
/// Module configuration for dependency injection.
/// Registers IGameApiClient as a singleton for accessing Unity services like Secret Manager.
/// </summary>
public class ModuleConfig : ICloudCodeSetup
{
	public void Setup(ICloudCodeConfig config)
	{
		config.Dependencies.AddSingleton(GameApiClient.Create());
		config.Dependencies.AddScoped<IEdgegapHttpClientFactory, EdgegapHttpClientFactory>();
	}
}

public class EdgegapAllocator(IGameApiClient gameApiClient, IEdgegapHttpClientFactory httpClientFactory, ILogger<EdgegapAllocator> logger) : IMatchmakerAllocator
{
	// Configuration - users should modify these constants for their setup
	private const string ApplicationName = "MyApp"; // TODO: Replace with actual application name
	private const string VersionName = "MyVersion"; // TODO: Replace with actual version name
	private const string PortName = "gameport"; // TODO: Replace with actual port name

	// Edgegap Constants
	private const string EdgegapApiUrl = "https://api.edgegap.com";

	// Secret names - these must match the secrets stored in Unity Dashboard
	private const string EdgegapApiTokenSecretName = "EDGEGAP_API_TOKEN";

	[CloudCodeFunction("Matchmaker_AllocateServer")]
	public async Task<AllocateResponse> Allocate(IExecutionContext context, AllocateRequest request)
	{
		try
		{
			Secret edgegapApiToken = await gameApiClient.SecretManager.GetSecret(context, EdgegapApiTokenSecretName);
			using HttpClient client = httpClientFactory.CreateClient(edgegapApiToken.Value);
			logger.LogInformation(
				"MatchProperties JSON:\n{json}",
				JsonSerializer.Serialize(
					request.MatchmakingResults.MatchProperties,
					new JsonSerializerOptions
					{
						WriteIndented = true
					}
				)
			);

			var deploymentRequest = new DeploymentRequest
			{
				Application = ApplicationName,
				Version = VersionName,
				RequireCachedLocations = false,
				Users =
				[
					new DeploymentUser
					{
						UserType = "ip_address",
						UserData = new UserData
						{
							IpAddress = "8.8.8.8",
						},
					},
				],
				EnvironmentVariables =
				[
					new EnvironmentVariable
					{
						Key = "MATCH_ID",
						Value = request.MatchId,
						IsHidden = false,
					},
				],
				Tags =
				[
					"ugs-matchmaker",
				]
			};

			var content = new StringContent(JsonConvert.SerializeObject(deploymentRequest), Encoding.UTF8, "application/json");
			HttpResponseMessage response = await client.PostAsync($"{EdgegapApiUrl}/v2/deployments", content);

			string responseContent = await response.Content.ReadAsStringAsync();
			if (!response.IsSuccessStatusCode)
			{
				logger.LogError("Edgegap deployment failed with status code {ResponseStatusCode}: {ResponseContent}", response.StatusCode, responseContent);
				return new AllocateResponse(AllocateStatus.Error)
				{
					Message = responseContent
				};
			}

			var edgegapDeployment = JsonConvert.DeserializeObject<DeploymentResponse>(responseContent);

			return new AllocateResponse(AllocateStatus.Created)
			{
				AllocationData = new Dictionary<string, object>
				{
					{
						"requestId", edgegapDeployment?.RequestId ?? string.Empty
					},
				},
			};
		}
		catch (Exception e)
		{
			logger.LogError(e, "Edgegap deployment failed");
			return new AllocateResponse(AllocateStatus.Error)
			{
				Message = e.Message,
			};
		}
	}

	[CloudCodeFunction("Matchmaker_PollAllocation")]
	public async Task<PollResponse> Poll(IExecutionContext context, PollRequest request)
	{
		var requestId = request.AllocationData["requestId"].ToString();
		try
		{
			Secret edgegapApiToken = await gameApiClient.SecretManager.GetSecret(context, EdgegapApiTokenSecretName);
			HttpClient client = httpClientFactory.CreateClient(edgegapApiToken.Value);
			HttpResponseMessage response = await client.GetAsync($"{EdgegapApiUrl}/v1/status/{requestId}");
			string responseContent = await response.Content.ReadAsStringAsync();

			if (!response.IsSuccessStatusCode)
			{
				return new PollResponse(PollStatus.Error)
				{
					Message = responseContent,
				};
			}

			var deploymentStatus = JsonConvert.DeserializeObject<DeploymentStatusResponse>(responseContent);

			if (deploymentStatus == null)
			{
				return new PollResponse(PollStatus.Error)
				{
					Message = "Deployment status response is null",
				};
			}

			switch (deploymentStatus.CurrentStatus)
			{
				case DeploymentStatus.Ready:
					Port? port = deploymentStatus.Ports?[PortName];
					if (deploymentStatus.PublicIp == null || port == null)
					{
						return new PollResponse(PollStatus.Error)
						{
							Message = $"Deployment status response is missing port or public IP: {responseContent}",
						};
					}

					return new PollResponse(PollStatus.Allocated)
					{
						AssignmentData = AssignmentData.IpPort(deploymentStatus.PublicIp, port.External),
					};
				case DeploymentStatus.Error:
					return new PollResponse(PollStatus.Error)
					{
						Message = $"Deployment failed with the current error: {deploymentStatus.ErrorDetail}",
					};
				case DeploymentStatus.Terminating:
				case DeploymentStatus.Terminated:
					return new PollResponse(PollStatus.Error)
					{
						Message = "Deployment is terminated or terminated and can't receive connection anymore",
					};
				case DeploymentStatus.Deploying:
				case DeploymentStatus.Seeking:
				default:
					return new PollResponse(PollStatus.Pending);
			}
		}
		catch (Exception e)
		{
			logger.LogError(e, "Error polling Edgegap");
			return new PollResponse(PollStatus.Error)
			{
				Message = e.Message,
			};
		}
	}
}
