// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System.ComponentModel.DataAnnotations;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

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

        public async Task<IActionResult> OnGetAsync()
        {
            //If user exists in the database redirect to login
            if (_signInManager.UserManager.Users.Any())
            {
                return RedirectToPage("./Login");
            }

            if (!string.IsNullOrEmpty(ErrorMessage))
            {
                ModelState.AddModelError(string.Empty, ErrorMessage);
            }

            return Page();
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