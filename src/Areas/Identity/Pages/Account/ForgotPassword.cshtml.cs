// NodeGuard
// Copyright (C) 2025  Elenpay
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY, without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see http://www.gnu.org/licenses/.

#nullable disable

using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using NodeGuard.Data.Models;

namespace NodeGuard.Areas.Identity.Pages.Account
{
    public class ForgotPasswordModel : PageModel
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IEmailSender _emailSender;

        public ForgotPasswordModel(UserManager<ApplicationUser> userManager, IEmailSender emailSender)
        {
            _userManager = userManager;
            _emailSender = emailSender;
        }

        /// <summary>
        ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        [BindProperty]
        public InputModel Input { get; set; }

        /// <summary>
        ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public class InputModel
        {
            /// <summary>
            ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
            ///     directly from your code. This API may change or be removed in future releases.
            /// </summary>
            [Required]
            [EmailAddress]
            public string Email { get; set; }
        }

        public Task<IActionResult> OnPostAsync()
        {
            return Task.FromResult<IActionResult>(NotFound());

            //if (ModelState.IsValid)
            //{
            //    var user = await _userManager.FindByEmailAsync(Input.Email);
            //    if (user == null || !(await _userManager.IsEmailConfirmedAsync(user)))
            //    {
            //        // Don't reveal that the user does not exist or is not confirmed
            //        return RedirectToPage("./ForgotPasswordConfirmation");
            //    }

            //    // For more information on how to enable account confirmation and password reset please
            //    // visit https://go.microsoft.com/fwlink/?LinkID=532713
            //    var code = await _userManager.GeneratePasswordResetTokenAsync(user);
            //    code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(code));
            //    var callbackUrl = Url.Page(
            //        "/Account/ResetPassword",
            //        pageHandler: null,
            //        values: new { area = "Identity", code },
            //        protocol: Request.Scheme);

            //    await _emailSender.SendEmailAsync(
            //        Input.Email,
            //        "Reset Password",
            //        $"Please reset your password by <a href='{HtmlEncoder.Default.Encode(callbackUrl)}'>clicking here</a>.");

            //    return RedirectToPage("./ForgotPasswordConfirmation");
            //}

            //return Page();
        }
    }
}
