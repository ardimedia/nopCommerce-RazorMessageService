using Nop.Core.Plugins;
using Nop.Services.Tasks;
using ToSic.Nop.Plugins.RazorMessageService.ScheduledTasks;

namespace ToSic.Nop.Plugins.RazorMessageService
{
	public class RazorMessageServicePlugin : BasePlugin
	{
		private readonly IScheduleTaskService _scheduleTaskService;

		public RazorMessageServicePlugin(IScheduleTaskService scheduleTaskService)
		{
            this._scheduleTaskService = scheduleTaskService;
		}

		public override void Install()
		{
			CompileRazorMessagesTask.EnsureScheduleTask(this._scheduleTaskService);

			base.Install();
		}

		public override void Uninstall()
		{
			CompileRazorMessagesTask.RemoveScheduleTask(this._scheduleTaskService);

			base.Uninstall();
		}
	}
}
