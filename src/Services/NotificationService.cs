using FundsManager.Data;
using Microsoft.EntityFrameworkCore;
using OneSignalApi.Api;
using OneSignalApi.Client;
using OneSignalApi.Model;

namespace FundsManager.Services;

public class NotificationService
{
	private readonly DefaultApi _appInstance;
	private readonly ILogger<NotificationService> _logger;
	private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
	private readonly String _oneSignalApiId;
	private readonly String _notificationReturnUrl;
	
	public NotificationService(ILogger<NotificationService> logger, IDbContextFactory<ApplicationDbContext> dbContextFactory)
	{
		_logger = logger;
		_dbContextFactory = dbContextFactory;
		_oneSignalApiId = Environment.GetEnvironmentVariable("PUSH_NOTIFICATIONS_ONESIGNAL_APP_ID") ?? throw new InvalidOperationException();
		_notificationReturnUrl = Environment.GetEnvironmentVariable("FUNDSMANAGER_ENDPOINT") ?? throw new InvalidOperationException();
		
		var appConfig = new Configuration();
		appConfig.BasePath = Environment.GetEnvironmentVariable("PUSH_NOTIFICATIONS_ONESIGNAL_API_BASE_PATH");
		appConfig.AccessToken = Environment.GetEnvironmentVariable("PUSH_NOTIFICATIONS_ONESIGNAL_API_TOKEN");
		_appInstance = new DefaultApi(appConfig);
	}

	public async Task NotifyRequestSigners(int walletId, string sourcePage)
	{
		if (Convert.ToBoolean(Environment.GetEnvironmentVariable("PUSH_NOTIFICATIONS_ONESIGNAL_ENABLED")))
		{
			await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();
			String notificationReturnUrl = _notificationReturnUrl + sourcePage;
			List<String> notifiableUsersString = applicationDbContext.ApplicationUsers
				.Where(user => user.Keys.Any(key => key.Wallets.Any(wallet => wallet.Id == walletId)))
				.Select(user => user.Id)
				.ToList();
			_logger.LogInformation("Sending notifications to the following Ids: {UsersString}", notifiableUsersString);
			await SendNotification("There is a pending item awaiting approval. Click here to continue", notifiableUsersString, notificationReturnUrl);
		}
	}
	
	private async Task SendNotification(String message, List<String> recipientList, String returnUrl)
	{
		var notification = new Notification(appId:_oneSignalApiId)
		{
			Contents = new StringMap(en: message),
			IncludeExternalUserIds = recipientList,
			Url = returnUrl
		};

		var response = await _appInstance.CreateNotificationAsync(notification);
		_logger.LogInformation("Notification created for {ResponseRecipients} recipients", response.Recipients);
	}

}
