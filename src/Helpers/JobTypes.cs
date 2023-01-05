using Quartz;
using Quartz.Impl.Triggers;

namespace FundsManager.Helpers;

public class SimpleJob
{
    public static JobAndTrigger Create<T>(JobDataMap data, string identitySuffix) where T : IJob
    {
        var job = JobBuilder.Create<T>()
                    .WithIdentity($"{typeof(T).Name}-{identitySuffix}")
                    .SetJobData(data ?? new JobDataMap())
                    .Build();

        var trigger = TriggerBuilder.Create()
            .WithIdentity($"{typeof(T).Name}Trigger-{identitySuffix}")
            .StartNow()
            .Build();

        return new JobAndTrigger(job, trigger);
    }
}

public class RetriableJob
{
    public static JobAndTrigger Create<T>(JobDataMap data, string identitySuffix, int[]? intervalListInMinutes = null) where T : IJob
    {
        intervalListInMinutes = intervalListInMinutes ?? new int[] { 1, 5, 10, 20 };

        var map = data ?? new JobDataMap();
        map.Put("intervalListInMinutes", intervalListInMinutes);

        var job = JobBuilder.Create<T>()
                   .DisallowConcurrentExecution()
                   .SetJobData(map)
                   .WithIdentity($"{typeof(T).Name}-{identitySuffix}")
                   .Build();

        var trigger = TriggerBuilder.Create()
            .WithIdentity($"{typeof(T).Name}Trigger-{identitySuffix}")
            .StartNow()
            .WithSimpleSchedule(opts =>
                opts
                    .WithIntervalInMinutes(intervalListInMinutes[0])
                    .WithRepeatCount(intervalListInMinutes.Length))
            .Build();

        return new JobAndTrigger(job, trigger);
    }

    public static int[]? ParseRetryListFromEnvironmenVariable(string variable)
    {
        var retryListAsString = Environment.GetEnvironmentVariable(variable);
        return retryListAsString?
            .Split(",")
            .Select<string, int>(s => int.Parse(s))
            .ToArray();
    }
}

public class JobAndTrigger
{
    public IJobDetail Job { get; set; }
    public ITrigger Trigger { get; set; }

    public JobAndTrigger(IJobDetail job, ITrigger trigger)
    {
        if (job == null)
        {
            throw new Exception("Job parameter is needed");
        }
        if (trigger == null)
        {
            throw new Exception("Trigger parameter is needed");
        }
        Job = job;
        Trigger = trigger;
    }
}

public class JobRescheduler
{
    public static async Task SetNextInterval(IJobExecutionContext context)
    {
        var data = context.JobDetail.JobDataMap;
        var intervals = data.Get("intervalListInMinutes") as int[];
        if (intervals == null)
        {
            throw new Exception("No interval list found, make sure you're using the RetriableJob class");
        };

        var trigger = context.Trigger as SimpleTriggerImpl;

        if (trigger!.TimesTriggered >= intervals.Length) {
            return;
        }

        var repeatInterval =  intervals[trigger!.TimesTriggered - 1];

        var prevTriggerTime = trigger.GetPreviousFireTimeUtc();
        trigger.SetNextFireTimeUtc(prevTriggerTime!.Value.AddMinutes(repeatInterval));
        
        await context.Scheduler.RescheduleJob(context.Trigger.Key, trigger);
    }
}