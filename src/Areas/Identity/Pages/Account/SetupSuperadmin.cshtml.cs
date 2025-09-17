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
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;

namespace NodeGuard.Areas.Identity.Pages.Account
{
    public class SetupSuperadminModel : PageModel
    {
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly ILogger<SetupSuperadminModel> _logger;
        private readonly IApplicationUserRepository _applicationUserRepository;

        public SetupSuperadminModel(SignInManager<ApplicationUser> signInManager, ILogger<SetupSuperadminModel> logger,
            IApplicationUserRepository applicationUserRepository)
        {
            _signInManager = signInManager;
            _logger = logger;
            _applicationUserRepository = applicationUserRepository;
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
        public string ReturnUrl { get; set; }

        /// <summary>
        ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        [TempData]
        public string ErrorMessage { get; set; }

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
            public string Username { get; init; }

            /// <summary>
            ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
            ///     directly from your code. This API may change or be removed in future releases.
            /// </summary>
            [Required]
            [DataType(DataType.Password)]
            public string Password { get; init; }


            [Required]
            [DataType(DataType.Password)]
            [Display(Name = "Confirm password")]
            [Compare("Password", ErrorMessage = "The password and confirmation password do not match.")]
            public string ConfirmationPassword { get; init; }
        }

        public Task<IActionResult> OnGetAsync()
        {
            //If user exists in the database redirect to login
            if (_signInManager.UserManager.Users.Any())
            {
                return Task.FromResult<IActionResult>(RedirectToPage("./Login"));
            }

            if (!string.IsNullOrEmpty(ErrorMessage))
            {
                ModelState.AddModelError(string.Empty, ErrorMessage);
            }

            return Task.FromResult<IActionResult>(Page());
        }

        public async Task<IActionResult> OnPostAsync()
        {
            //If there are users in the system return to login
            if (_signInManager.UserManager.Users.Any())
            {
                return RedirectToPage("./Login");
            }

            if (ModelState.IsValid)
            {
                //Check for passwords match
                if (Input.ConfirmationPassword != Input.Password)
                {
                    ModelState.AddModelError(string.Empty, "Passwords do not match");
                    return Page();
                }

                //Create the user using the repository
                var superAdminAddResult =
                    await _applicationUserRepository.CreateSuperAdmin(Input.Username, Input.Password);
                if (!superAdminAddResult.Item1)
                {
                    _logger.LogError("Error while creating superadmin");
                    ModelState.AddModelError(string.Empty, "Error while creating superadmin");
                }
                else
                {
                    _logger.LogInformation("Superadmin created");


                    //Sign in the user

                    var result = await _signInManager.PasswordSignInAsync(Input.Username, Input.Password, true,
                        lockoutOnFailure: false);
                    if (result.Succeeded)
                    {
                        _logger.LogInformation("User logged in.");
                        return LocalRedirect("/");
                    }


                    return RedirectToPage("./Login");
                }
            }

            // If we got this far, something failed, redisplay form
            return Page();
        }
    }
}
