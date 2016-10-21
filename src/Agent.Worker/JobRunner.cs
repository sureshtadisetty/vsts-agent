using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Agent.Util;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;

namespace Microsoft.VisualStudio.Services.Agent.Worker
{
    [ServiceLocator(Default = typeof(JobRunner))]
    public interface IJobRunner : IAgentService
    {
        Task<TaskResult> RunAsync(AgentJobRequestMessage message, CancellationToken jobRequestCancellationToken);
    }

    public sealed class JobRunner : AgentService, IJobRunner
    {
        public async Task<TaskResult> RunAsync(AgentJobRequestMessage message, CancellationToken jobRequestCancellationToken)
        {
            // Validate parameters.
            Trace.Entering();
            ArgUtil.NotNull(message, nameof(message));
            ArgUtil.NotNull(message.Environment, nameof(message.Environment));
            ArgUtil.NotNull(message.Environment.Variables, nameof(message.Environment.Variables));
            ArgUtil.NotNull(message.Tasks, nameof(message.Tasks));
            Trace.Info("Job ID {0}", message.JobId);

            if (message.Environment.Variables.ContainsKey(Constants.Variables.System.EnableAccessToken) &&
                StringUtil.ConvertToBoolean(message.Environment.Variables[Constants.Variables.System.EnableAccessToken]))
            {
                // TODO: get access token use Util Method
                message.Environment.Variables[Constants.Variables.System.AccessToken] = message.Environment.SystemConnection.Authorization.Parameters["AccessToken"];
            }

            // Make sure SystemConnection Url and Endpoint Url match Config Url base
            ReplaceConfigUriBaseInJobRequestMessage(message);

            // Setup the job server and job server queue.
            var jobServer = HostContext.GetService<IJobServer>();
            var jobServerCredential = ApiUtil.GetVssCredential(message.Environment.SystemConnection);
            Uri jobServerUrl = message.Environment.SystemConnection.Url;

            Trace.Info($"Creating job server with URL: {jobServerUrl}");
            // jobServerQueue is the throttling reporter.
            var jobServerQueue = HostContext.GetService<IJobServerQueue>();
            var jobConnection = ApiUtil.CreateConnection(jobServerUrl, jobServerCredential, new DelegatingHandler[] { new ThrottlingReportHandler(jobServerQueue) });
            await jobServer.ConnectAsync(jobConnection);

            jobServerQueue.Start(message);

            IExecutionContext jobContext = null;
            try
            {
                // Create the job execution context.
                jobContext = HostContext.CreateService<IExecutionContext>();
                jobContext.InitializeJob(message, jobRequestCancellationToken);
                Trace.Info("Starting the job execution context.");
                jobContext.Start();

                // Set agent version into ExecutionContext's variables dictionary.
                jobContext.Variables.Set(Constants.Variables.Agent.Version, Constants.Agent.Version);

                // Print agent version into log for better diagnostic experience 
                jobContext.Output(StringUtil.Loc("AgentVersion", Constants.Agent.Version));

                // Print proxy setting information for better diagnostic experience
                var proxyConfig = HostContext.GetService<IProxyConfiguration>();
                if (!string.IsNullOrEmpty(proxyConfig.ProxyUrl))
                {
                    jobContext.Output(StringUtil.Loc("AgentRunningBehindProxy", proxyConfig.ProxyUrl));
                }

                // Validate directory permissions.
                string workDirectory = HostContext.GetDirectory(WellKnownDirectory.Work);
                Trace.Info($"Validating directory permissions for: '{workDirectory}'");
                try
                {
                    Directory.CreateDirectory(workDirectory);
                    IOUtil.ValidateExecutePermission(workDirectory);
                }
                catch (Exception ex)
                {
                    Trace.Error(ex);
                    jobContext.Error(ex);
                    return await CompleteJob(jobServer, jobContext, message, TaskResult.Failed);
                }

                // Set agent variables.
                AgentSettings settings = HostContext.GetService<IConfigurationStore>().GetSettings();
                jobContext.Variables.Set(Constants.Variables.Agent.Id, settings.AgentId.ToString(CultureInfo.InvariantCulture));
                jobContext.Variables.Set(Constants.Variables.Agent.HomeDirectory, IOUtil.GetRootPath());
                jobContext.Variables.Set(Constants.Variables.Agent.JobName, message.JobName);
                jobContext.Variables.Set(Constants.Variables.Agent.MachineName, Environment.MachineName);
                jobContext.Variables.Set(Constants.Variables.Agent.Name, settings.AgentName);
                jobContext.Variables.Set(Constants.Variables.Agent.RootDirectory, IOUtil.GetWorkPath(HostContext));
#if OS_WINDOWS
                jobContext.Variables.Set(Constants.Variables.Agent.ServerOMDirectory, Path.Combine(IOUtil.GetExternalsPath(), Constants.Path.ServerOMDirectory));
#endif
                jobContext.Variables.Set(Constants.Variables.Agent.WorkFolder, IOUtil.GetWorkPath(HostContext));
                jobContext.Variables.Set(Constants.Variables.System.WorkFolder, IOUtil.GetWorkPath(HostContext));

                // prefer task definitions url, then TFS collection url, then TFS account url
                var taskServer = HostContext.GetService<ITaskServer>();
                Uri taskServerUri = null;
                if (!string.IsNullOrEmpty(jobContext.Variables.System_TaskDefinitionsUri))
                {
                    taskServerUri = new Uri(jobContext.Variables.System_TaskDefinitionsUri);
                }
                else if (!string.IsNullOrEmpty(jobContext.Variables.System_TFCollectionUrl))
                {
                    taskServerUri = new Uri(jobContext.Variables.System_TFCollectionUrl);
                }

                var taskServerCredential = ApiUtil.GetVssCredential(message.Environment.SystemConnection);
                if (taskServerUri != null)
                {
                    Trace.Info($"Creating task server with {taskServerUri}");
                    await taskServer.ConnectAsync(ApiUtil.CreateConnection(taskServerUri, taskServerCredential));
                }

                if (taskServerUri == null || !await taskServer.TaskDefinitionEndpointExist(jobRequestCancellationToken))
                {
                    Trace.Info($"Can't determine task download url from JobMessage or the endpoint doesn't exist.");
                    var configStore = HostContext.GetService<IConfigurationStore>();
                    taskServerUri = new Uri(configStore.GetSettings().ServerUrl);
                    Trace.Info($"Recreate task server with configuration server url: {taskServerUri}");
                    await taskServer.ConnectAsync(ApiUtil.CreateConnection(taskServerUri, taskServerCredential));
                }

                // Expand the endpoint data values.
                foreach (ServiceEndpoint endpoint in jobContext.Endpoints)
                {
                    jobContext.Variables.ExpandValues(target: endpoint.Data);
                    VarUtil.ExpandEnvironmentVariables(HostContext, target: endpoint.Data);
                }

                // Get the job extensions.
                Trace.Info("Getting job extensions.");
                string hostType = jobContext.Variables.System_HostType;
                var extensionManager = HostContext.GetService<IExtensionManager>();
                IJobExtension[] extensions =
                    (extensionManager.GetExtensions<IJobExtension>() ?? new List<IJobExtension>())
                    .Where(x => string.Equals(x.HostType, hostType, StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                // Add the prepare steps.
                Trace.Info("Adding job prepare extensions.");
                List<IStep> steps = new List<IStep>();
                foreach (IJobExtension extension in extensions)
                {
                    if (extension.PrepareStep != null)
                    {
                        Trace.Verbose($"Adding {extension.GetType().Name}.{nameof(extension.PrepareStep)}.");
                        extension.PrepareStep.ExecutionContext = jobContext.CreateChild(Guid.NewGuid(), extension.PrepareStep.DisplayName);
                        steps.Add(extension.PrepareStep);
                    }
                }

                // Add the task steps.
                Trace.Info("Adding tasks.");
                foreach (TaskInstance taskInstance in message.Tasks)
                {
                    Trace.Verbose($"Adding {taskInstance.DisplayName}.");
                    var taskRunner = HostContext.CreateService<ITaskRunner>();
                    taskRunner.ExecutionContext = jobContext.CreateChild(taskInstance.InstanceId, taskInstance.DisplayName);
                    taskRunner.TaskInstance = taskInstance;
                    steps.Add(taskRunner);
                }

                // Add the finally steps.
                Trace.Info("Adding job finally extensions.");
                foreach (IJobExtension extension in extensions)
                {
                    if (extension.FinallyStep != null)
                    {
                        Trace.Verbose($"Adding {extension.GetType().Name}.{nameof(extension.FinallyStep)}.");
                        extension.FinallyStep.ExecutionContext = jobContext.CreateChild(Guid.NewGuid(), extension.FinallyStep.DisplayName);
                        steps.Add(extension.FinallyStep);
                    }
                }

                // Download tasks if not already in the cache
                Trace.Info("Downloading task definitions.");
                var taskManager = HostContext.GetService<ITaskManager>();
                try
                {
                    await taskManager.DownloadAsync(jobContext, message.Tasks);
                }
                catch (OperationCanceledException ex)
                {
                    // set the job to canceled
                    Trace.Error($"Caught exception: {ex}");
                    jobContext.Error(ex);
                    return await CompleteJob(jobServer, jobContext, message, TaskResult.Canceled);
                }
                catch (Exception ex)
                {
                    // Log the error and fail the job.
                    Trace.Error($"Caught exception from {nameof(TaskManager)}: {ex}");
                    jobContext.Error(ex);
                    return await CompleteJob(jobServer, jobContext, message, TaskResult.Failed);
                }

                // Run the steps.
                var stepsRunner = HostContext.GetService<IStepsRunner>();
                try
                {
                    await stepsRunner.RunAsync(jobContext, steps);
                }
                catch (OperationCanceledException ex)
                {
                    // set the job to canceled
                    Trace.Error($"Caught exception: {ex}");
                    jobContext.Error(ex);
                    return await CompleteJob(jobServer, jobContext, message, TaskResult.Canceled);
                }
                catch (Exception ex)
                {
                    // Log the error and fail the job.
                    Trace.Error($"Caught exception from {nameof(StepsRunner)}: {ex}");
                    jobContext.Error(ex);
                    return await CompleteJob(jobServer, jobContext, message, TaskResult.Failed);
                }

                Trace.Info($"Job result: {jobContext.Result}");

                // Complete the job.
                Trace.Info("Completing the job execution context.");
                return await CompleteJob(jobServer, jobContext, message);
            }
            finally
            {
                // Drain the job server queue.
                if (jobServerQueue != null)
                {
                    try
                    {
                        Trace.Info("Shutting down the job server queue.");
                        await jobServerQueue.ShutdownAsync();
                    }
                    catch (Exception ex)
                    {
                        Trace.Error($"Caught exception from {nameof(JobServerQueue)}.{nameof(jobServerQueue.ShutdownAsync)}: {ex}");
                    }
                }
            }
        }

        private static async Task<TaskResult> CompleteJob(IJobServer jobServer, IExecutionContext jobContext, AgentJobRequestMessage message, TaskResult? taskResult = null)
        {
            var result = jobContext.Complete(taskResult);
            var outputVariables = jobContext.Variables.GetOutputVariables();
            var webApiVariables = ToWebApiVariables(outputVariables);
            var jobCompletedEvent = new JobCompletedEvent(message.RequestId, message.JobId, result, webApiVariables.ToList());
            await jobServer.RaisePlanEventAsync(message.Plan.ScopeIdentifier, message.Plan.PlanType, message.Plan.PlanId, jobCompletedEvent, default(CancellationToken));
            return result;
        }

        private static IEnumerable<TeamFoundation.DistributedTask.WebApi.Variable> ToWebApiVariables(IEnumerable<Variable> outputVariables)
        {
            if (outputVariables == null || !outputVariables.Any())
            {
                return new List<TeamFoundation.DistributedTask.WebApi.Variable>();
            }

            var variables = outputVariables.Select(
                v =>
                    new TeamFoundation.DistributedTask.WebApi.Variable
                    {
                        Name = v.Name,
                        Value = v.Value,
                        Secret = v.Secret
                    });

            return variables;
        }

        // the hostname (how the agent knows the server) is external to our server
        // in other words, an agent may have it's own way (DNS, hostname) of refering
        // to the server.  it owns that.  That's the hostname we will use.
        // Example: Server's notification url is http://tfsserver:8080/tfs 
        //          Agent config url is http://tfsserver.mycompany.com:8080/tfs 
        private Uri ReplaceWithConfigUriBase(Uri messageUri)
        {
            AgentSettings settings = HostContext.GetService<IConfigurationStore>().GetSettings();
            try
            {
                if (UrlUtil.IsHosted(messageUri.AbsoluteUri))
                {
                    // If messageUri is hosted service URL, return the messageUri as it is.
                    return messageUri;
                }

                var configUri = new Uri(settings.ServerUrl);
                Uri result = null;
                Uri configBaseUri = null;
                string scheme = messageUri.GetComponents(UriComponents.Scheme, UriFormat.Unescaped);
                string host = configUri.GetComponents(UriComponents.Host, UriFormat.Unescaped);

                int portValue = 0;
                string port = messageUri.GetComponents(UriComponents.Port, UriFormat.Unescaped);
                if (!string.IsNullOrEmpty(port))
                {
                    int.TryParse(port, out portValue);
                }

                configBaseUri = portValue > 0 ? new UriBuilder(scheme, host, portValue).Uri : new UriBuilder(scheme, host).Uri;

                if (Uri.TryCreate(configBaseUri, messageUri.PathAndQuery, out result))
                {
                    //replace the schema and host portion of messageUri with the host from the
                    //server URI (which was set at config time)
                    return result;
                }
            }
            catch (InvalidOperationException ex)
            {
                //cannot parse the Uri - not a fatal error
                Trace.Error(ex);
            }
            catch (UriFormatException ex)
            {
                //cannot parse the Uri - not a fatal error
                Trace.Error(ex);
            }

            return messageUri;
        }

        private void ReplaceConfigUriBaseInJobRequestMessage(AgentJobRequestMessage message)
        {
            string systemConnectionHostName = message.Environment.SystemConnection.Url.GetComponents(UriComponents.Host, UriFormat.Unescaped);
            // fixup any endpoint Url that match SystemConnect host.
            foreach (var endpoint in message.Environment.Endpoints)
            {
                if (endpoint.Url.GetComponents(UriComponents.Host, UriFormat.Unescaped).Equals(systemConnectionHostName, StringComparison.OrdinalIgnoreCase))
                {
                    endpoint.Url = ReplaceWithConfigUriBase(endpoint.Url);
                    Trace.Info($"Ensure endpoint url match config url base. {endpoint.Url}");
                }
            }

            // fixup well known variables. (taskDefinitionsUrl, tfsServerUrl, tfsCollectionUrl)
            if (message.Environment.Variables.ContainsKey(WellKnownDistributedTaskVariables.TaskDefinitionsUrl))
            {
                string taskDefinitionsUrl = message.Environment.Variables[WellKnownDistributedTaskVariables.TaskDefinitionsUrl];
                message.Environment.Variables[WellKnownDistributedTaskVariables.TaskDefinitionsUrl] = ReplaceWithConfigUriBase(new Uri(taskDefinitionsUrl)).AbsoluteUri;
                Trace.Info($"Ensure System.TaskDefinitionsUrl match config url base. {message.Environment.Variables[WellKnownDistributedTaskVariables.TaskDefinitionsUrl]}");
            }

            if (message.Environment.Variables.ContainsKey(WellKnownDistributedTaskVariables.TFCollectionUrl))
            {
                string tfsCollectionUrl = message.Environment.Variables[WellKnownDistributedTaskVariables.TFCollectionUrl];
                message.Environment.Variables[WellKnownDistributedTaskVariables.TFCollectionUrl] = ReplaceWithConfigUriBase(new Uri(tfsCollectionUrl)).AbsoluteUri;
                Trace.Info($"Ensure System.TFCollectionUrl match config url base. {message.Environment.Variables[WellKnownDistributedTaskVariables.TFCollectionUrl]}");
            }

            // fixup SystemConnection Url
            message.Environment.SystemConnection.Url = ReplaceWithConfigUriBase(message.Environment.SystemConnection.Url);
            Trace.Info($"Ensure SystemConnection url match config url base. {message.Environment.SystemConnection.Url}");

            // back compat server url
            message.Environment.Variables[Constants.Variables.System.TFServerUrl] = message.Environment.SystemConnection.Url.AbsoluteUri;
            Trace.Info($"Ensure System.TFServerUrl match config url base. {message.Environment.SystemConnection.Url.AbsoluteUri}");
        }
    }
}
