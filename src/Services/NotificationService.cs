/*
 * NodeGuard
 * Copyright (C) 2023  Elenpay
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program.  If not, see http://www.gnu.org/licenses/.
 *
 */

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
		_oneSignalApiId = Constants.PUSH_NOTIFICATIONS_ONESIGNAL_APP_ID;
		_notificationReturnUrl = Constants.FUNDSMANAGER_ENDPOINT;
		
		var appConfig = new Configuration();
		appConfig.BasePath = Constants.PUSH_NOTIFICATIONS_ONESIGNAL_API_BASE_PATH;
		appConfig.AccessToken = Constants.PUSH_NOTIFICATIONS_ONESIGNAL_API_TOKEN;
		_appInstance = new DefaultApi(appConfig);
	}

	public async Task NotifyRequestSigners(int walletId, string sourcePage)
	{
		if (Constants.PUSH_NOTIFICATIONS_ONESIGNAL_ENABLED)
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
