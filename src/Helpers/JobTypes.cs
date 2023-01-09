using FundsManager.Jobs;
using Quartz;

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
    public static JobAndTrigger Create<T>(JobDataMap data, string identitySuffix, int intervalInSeconds = 10, int retryTimes = 20) where T : IJob
    {
        var job = JobBuilder.Create<T>()
                   .DisallowConcurrentExecution()
                   .SetJobData(data ?? new JobDataMap())
                   .WithIdentity($"{typeof(T).Name}-{identitySuffix}")
                   .Build();

        var trigger = TriggerBuilder.Create()
            .WithIdentity($"{typeof(T).Name}Trigger-{identitySuffix}")
            .StartNow()
            .WithSimpleSchedule(opts =>
                opts
                    .WithIntervalInSeconds(intervalInSeconds)
                    .WithRepeatCount(retryTimes))
            .Build();

        return new JobAndTrigger(job, trigger);
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