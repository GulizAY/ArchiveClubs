﻿using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using IdentityServer.API.Model;
using IdentityServer.Infrastructure.Persistence;
using IdentityServer4.Events;
using IdentityServer4.Models;
using IdentityServer4.Services;
using IdentityServer4.Stores;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using IdentityServer4;
using IdentityServer.API.Helpers;
using Microsoft.AspNetCore.Authorization;
using IdentityServer4.Extensions;
using IdentityModel;

namespace IdentityServer.API.Controllers
{
  [SecurityHeaders]
  [AllowAnonymous]
  public class AccountController : Controller
  {
    private readonly UserManager<AppUser> _userManager;
    private readonly SignInManager<AppUser> _signInManager;
    private readonly IIdentityServerInteractionService _interaction;
    private readonly IAuthenticationSchemeProvider _schemeProvider;
    private readonly IClientStore _clientStore;
    private readonly IEventService _events;

    public AccountController(UserManager<AppUser> userManager, SignInManager<AppUser> signInManager, IIdentityServerInteractionService interaction, IAuthenticationSchemeProvider schemeProvider, IClientStore clientStore, IEventService events)
    {
      _userManager = userManager;
      _signInManager = signInManager;
      _interaction = interaction;
      _schemeProvider = schemeProvider;
      _clientStore = clientStore;
      _events = events;
    }

    /// <summary>
    /// Entry point into the login workflow
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Login(string returnUrl)
    {
      // build a model so we know what to show on the login page
      var vm = await BuildLoginViewModelAsync(returnUrl);

      if (vm.IsExternalLoginOnly)
      {
        // we only have one option for logging in and it's an external provider
        return RedirectToAction("Challenge", "External", new { scheme = vm.ExternalLoginScheme, returnUrl });
      }

      return View(vm);
    }

    /// <summary>
    /// Handle postback from username/password login
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginInputModel model, string button)
    {
      // check if we are in the context of an authorization request
      var context = await _interaction.GetAuthorizationContextAsync(model.ReturnUrl);

      // the user clicked the "cancel" button
      if (button != "login")
      {
        if (context != null)
        {
          // if the user cancels, send a result back into IdentityServer as if they 
          // denied the consent (even if this client does not require consent).
          // this will send back an access denied OIDC error response to the client.
          await _interaction.DenyAuthorizationAsync(context, AuthorizationError.AccessDenied);

          // we can trust model.ReturnUrl since GetAuthorizationContextAsync returned non-null
          if (context.IsNativeClient())
          {
            // The client is native, so this change in how to
            // return the response is for better UX for the end user.
            return this.LoadingPage("Redirect", model.ReturnUrl);
          }

          return Redirect(model.ReturnUrl);
        }
        else
        {
          // since we don't have a valid context, then we just go back to the home page
          return Redirect("~/");
        }
      }

      if (ModelState.IsValid)
      {
        var result = await _signInManager.PasswordSignInAsync(model.Username, model.Password, model.RememberLogin, lockoutOnFailure: true);
        if (result.Succeeded)
        {
          var user = await _userManager.FindByNameAsync(model.Username);
          await _events.RaiseAsync(new UserLoginSuccessEvent(user.UserName, user.Id.ToString(), user.UserName, clientId: context?.Client.ClientId));

          if (context != null)
          {
            if (context.IsNativeClient())
            {
              // The client is native, so this change in how to
              // return the response is for better UX for the end user.
              return this.LoadingPage("Redirect", model.ReturnUrl);
            }

            // we can trust model.ReturnUrl since GetAuthorizationContextAsync returned non-null
            //return Redirect(model.ReturnUrl);
            return Redirect(context.RedirectUri);
          }

          // request for a local page
          if (Url.IsLocalUrl(model.ReturnUrl))
          {
            return Redirect(model.ReturnUrl);
          }
          else if (string.IsNullOrEmpty(model.ReturnUrl))
          {
            return Redirect("~/");
          }
          else
          {
            // user might have clicked on a malicious link - should be logged
            throw new Exception("invalid return URL");
          }
        }

        await _events.RaiseAsync(new UserLoginFailureEvent(model.Username, "invalid credentials", clientId: context?.Client.ClientId));
        ModelState.AddModelError(string.Empty, AccountOptions.InvalidCredentialsErrorMessage);
      }

      // something went wrong, show form with error
      var vm = await BuildLoginViewModelAsync(model);
      return View(vm);
    }


    /// <summary>
    /// Show logout page
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Logout(string logoutId)
    {
      // build a model so the logout page knows what to display
      var vm = await BuildLogoutViewModelAsync(logoutId);

      if (vm.ShowLogoutPrompt == false)
      {
        // if the request for logout was properly authenticated from IdentityServer, then
        // we don't need to show the prompt and can just log the user out directly.
        return await Logout(vm);
      }

      return View(vm);
    }

    /// <summary>
    /// Handle logout page postback
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout(LogoutInputModel model)
    {
      // build a model so the logged out page knows what to display
      var vm = await BuildLoggedOutViewModelAsync(model.LogoutId);

      if (User?.Identity.IsAuthenticated == true)
      {
        // delete local authentication cookie
        await _signInManager.SignOutAsync();

        // raise the logout event
        await _events.RaiseAsync(new UserLogoutSuccessEvent(User.GetSubjectId(), User.GetDisplayName()));
      }

      // check if we need to trigger sign-out at an upstream identity provider
      if (vm.TriggerExternalSignout)
      {
        // build a return URL so the upstream provider will redirect back
        // to us after the user has logged out. this allows us to then
        // complete our single sign-out processing.
        string url = Url.Action("Logout", new { logoutId = vm.LogoutId });

        // this triggers a redirect to the external provider for sign-out
        return SignOut(new AuthenticationProperties { RedirectUri = url }, vm.ExternalAuthenticationScheme);
      }

      return View("LoggedOut", vm);
    }

    /// <summary>
    /// Handle register user
    /// </summary>
    /// <param name="request"></param>
    /// <returns></returns>
    public async Task<IActionResult> Register([FromBody] RegisterRequestViewModel request)
    {
      if (!ModelState.IsValid)
        return BadRequest(ModelState);

      var user = new AppUser { UserName = request.Email, Name = request.Name, Email = request.Email };
      var result = await _userManager.CreateAsync(user, request.Password);
      if (!result.Succeeded)
        return BadRequest(result.Errors);

      // default claims
      await _userManager.AddClaimsAsync(user, new Claim[]{
                            new Claim(JwtClaimTypes.Name, user.Name),
                            new Claim(JwtClaimTypes.PreferredUserName, user.UserName),
                            new Claim(JwtClaimTypes.Email, user.Email),
                            new Claim(JwtClaimTypes.Role, "archiveuser"),
                        });
      return Ok();
    }

    /// <summary>
    /// AccessDenied
    /// </summary>
    /// <returns></returns>
    [HttpGet]
    public IActionResult AccessDenied()
    {
      return View();
    }


    /*****************************************/
    /* helper APIs for the AccountController */
    /*****************************************/
    private async Task<LoginViewModel> BuildLoginViewModelAsync(string returnUrl)
    {
      var context = await _interaction.GetAuthorizationContextAsync(returnUrl);
      if (context?.IdP != null && await _schemeProvider.GetSchemeAsync(context.IdP) != null)
      {
        var local = context.IdP == IdentityServer4.IdentityServerConstants.LocalIdentityProvider;

        // this is meant to short circuit the UI and only trigger the one external IdP
        var vm = new LoginViewModel
        {
          EnableLocalLogin = local,
          ReturnUrl = returnUrl,
          Username = context?.LoginHint,
        };

        if (!local)
        {
          vm.ExternalProviders = new[] { new ExternalProvider { AuthenticationScheme = context.IdP } };
        }

        return vm;
      }

      var schemes = await _schemeProvider.GetAllSchemesAsync();

      var providers = schemes
          .Where(x => x.DisplayName != null)
          .Select(x => new ExternalProvider
          {
            DisplayName = x.DisplayName ?? x.Name,
            AuthenticationScheme = x.Name
          }).ToList();

      var allowLocal = true;
      if (context?.Client.ClientId != null)
      {
        var client = await _clientStore.FindEnabledClientByIdAsync(context.Client.ClientId);
        if (client != null)
        {
          allowLocal = client.EnableLocalLogin;

          if (client.IdentityProviderRestrictions != null && client.IdentityProviderRestrictions.Any())
          {
            providers = providers.Where(provider => client.IdentityProviderRestrictions.Contains(provider.AuthenticationScheme)).ToList();
          }
        }
      }

      return new LoginViewModel
      {
        AllowRememberLogin = AccountOptions.AllowRememberLogin,
        EnableLocalLogin = allowLocal && AccountOptions.AllowLocalLogin,
        ReturnUrl = returnUrl,
        Username = context?.LoginHint,
        ExternalProviders = providers.ToArray()
      };
    }

    private async Task<LoginViewModel> BuildLoginViewModelAsync(LoginInputModel model)
    {
      var vm = await BuildLoginViewModelAsync(model.ReturnUrl);
      vm.Username = model.Username;
      vm.RememberLogin = model.RememberLogin;
      return vm;
    }

    private async Task<LogoutViewModel> BuildLogoutViewModelAsync(string logoutId)
    {
      var vm = new LogoutViewModel { LogoutId = logoutId, ShowLogoutPrompt = AccountOptions.ShowLogoutPrompt };

      if (User?.Identity.IsAuthenticated != true)
      {
        // if the user is not authenticated, then just show logged out page
        vm.ShowLogoutPrompt = false;
        return vm;
      }

      var context = await _interaction.GetLogoutContextAsync(logoutId);
      if (context?.ShowSignoutPrompt == false)
      {
        // it's safe to automatically sign-out
        vm.ShowLogoutPrompt = false;
        return vm;
      }

      // show the logout prompt. this prevents attacks where the user
      // is automatically signed out by another malicious web page.
      return vm;
    }

    private async Task<LoggedOutViewModel> BuildLoggedOutViewModelAsync(string logoutId)
    {
      // get context information (client name, post logout redirect URI and iframe for federated signout)
      var logout = await _interaction.GetLogoutContextAsync(logoutId);

      var vm = new LoggedOutViewModel
      {
        AutomaticRedirectAfterSignOut = AccountOptions.AutomaticRedirectAfterSignOut,
        PostLogoutRedirectUri = logout?.PostLogoutRedirectUri,
        ClientName = string.IsNullOrEmpty(logout?.ClientName) ? logout?.ClientId : logout?.ClientName,
        SignOutIframeUrl = logout?.SignOutIFrameUrl,
        LogoutId = logoutId
      };

      if (User?.Identity.IsAuthenticated == true)
      {
        var idp = User.FindFirst(JwtClaimTypes.IdentityProvider)?.Value;
        if (idp != null && idp != IdentityServerConstants.LocalIdentityProvider)
        {
          var providerSupportsSignout = await HttpContext.GetSchemeSupportsSignOutAsync(idp);
          if (providerSupportsSignout)
          {
            if (vm.LogoutId == null)
            {
              // if there's no current logout context, we need to create one
              // this captures necessary info from the current logged in user
              // before we signout and redirect away to the external IdP for signout
              vm.LogoutId = await _interaction.CreateLogoutContextAsync();
            }

            vm.ExternalAuthenticationScheme = idp;
          }
        }
      }

      return vm;
    }

    /*
    [HttpPost("/api/[controller]/register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequestViewModel request)
    {
      if (!ModelState.IsValid)
        return BadRequest(ModelState);

      var user = new AppUser { UserName = request.Email, Name = request.Name, Email = request.Email };
      var result = await _userManager.CreateAsync(user, request.Password);
      if (!result.Succeeded)
        return BadRequest(result.Errors);

      await _userManager.AddClaimAsync(user, new Claim("userName", user.UserName));
      await _userManager.AddClaimAsync(user, new Claim("name", user.Name));
      await _userManager.AddClaimAsync(user, new Claim("email", user.Email));
      await _userManager.AddClaimAsync(user, new Claim("role", "archiveuser"));
      return Ok();
    }

    #region Web Kısımları

    /// <summary>
    /// Oturum açma akış
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Login(string returnUrl)
    {
      // build a model so we know what to show on the login page
      var vm = await BuildLoginViewModelAsync(returnUrl);

      if (vm.IsExternalLoginOnly)
      {
        // we only have one option for logging in and it's an external provider
        return RedirectToAction("Challenge", "External", new { provider = vm.ExternalLoginScheme, returnUrl });
      }

      return View(vm);
    }

    /// <summary>
    /// Handle postback from username/password login
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginInputModel model, string button)
    {
      // check if we are in the context of an authorization request
      AuthorizationRequest context = await _interaction.GetAuthorizationContextAsync(model.ReturnUrl);

      // the user clicked the "cancel" button
      if (button != "login")
      {
        if (context != null)
        {
          // if the user cancels, send a result back into IdentityServer as if they
          // denied the consent (even if this client does not require consent).
          // this will send back an access denied OIDC error response to the client.
          await _interaction.DenyAuthorizationAsync(context, AuthorizationError.AccessDenied);

          // we can trust model.ReturnUrl since GetAuthorizationContextAsync returned non-null
          if (context.IsNativeClient())
          {
            // The client is native, so this change in how to
            // return the response is for better UX for the end user.
            return this.LoadingPage("Redirect", model.ReturnUrl);
          }

          return Redirect(model.ReturnUrl);
        }
        else
        {
          // since we don't have a valid context, then we just go back to the home page
          return Redirect("~/");
        }
      }

      if (ModelState.IsValid)
      {
        // validate username/password
        var user = await _userManager.FindByNameAsync(model.Username);
        if (user != null && await _userManager.CheckPasswordAsync(user, model.Password))
        {
          await _events.RaiseAsync(new UserLoginSuccessEvent(user.UserName, user.Id.ToString(), user.Name));

          // only set explicit expiration here if user chooses "remember me".
          // otherwise we rely upon expiration configured in cookie middleware.
          AuthenticationProperties props = null;
          if (AccountOptions.AllowRememberLogin && model.RememberLogin)
          {
            props = new AuthenticationProperties
            {
              IsPersistent = true,
              ExpiresUtc = DateTimeOffset.UtcNow.Add(AccountOptions.RememberMeLoginDuration)
            };
          };

          // issue authentication cookie with subject ID and username
          var isuser = new IdentityServerUser(user.Id.ToString())
          {
            DisplayName = user.UserName
          };

          await HttpContext.SignInAsync(isuser, props);

          if (context != null)
          {
            if (context.IsNativeClient())
            {
              // The client is native, so this change in how to
              // return the response is for better UX for the end user.
              return this.LoadingPage("Redirect", model.ReturnUrl);
            }

            // we can trust model.ReturnUrl since GetAuthorizationContextAsync returned non-null
            return Redirect(model.ReturnUrl);
          }

          // request for a local page
          if (Url.IsLocalUrl(model.ReturnUrl))
          {
            return Redirect(model.ReturnUrl);
          }
          else if (string.IsNullOrEmpty(model.ReturnUrl))
          {
            return Redirect("~/");
          }
          else
          {
            // user might have clicked on a malicious link - should be logged
            throw new Exception("invalid return URL");
          }
        }

        await _events.RaiseAsync(new UserLoginFailureEvent(model.Username, "invalid credentials"));
        ModelState.AddModelError(string.Empty, AccountOptions.InvalidCredentialsErrorMessage);
      }

      // something went wrong, show form with error
      var vm = await BuildLoginViewModelAsync(model);
      return View(vm);
    }

    /// <summary>
    /// Show logout page
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Logout(string logoutId)
    {
      await _signInManager.SignOutAsync();
      var context = await _interaction.GetLogoutContextAsync(logoutId);
      return Redirect(context.PostLogoutRedirectUri);
    }

    /// <summary>
    /// helper APIs for the AccountController
    /// </summary>
    /// <param name="returnUrl"></param>
    /// <returns></returns>
    private async Task<LoginViewModel> BuildLoginViewModelAsync(string returnUrl)
    {
      var context = await _interaction.GetAuthorizationContextAsync(returnUrl);
      if (context?.IdP != null)
      {
        var local = context.IdP == IdentityServerConstants.LocalIdentityProvider;

        // this is meant to short circuit the UI and only trigger the one external IdP
        var vm = new LoginViewModel
        {
          EnableLocalLogin = local,
          ReturnUrl = returnUrl,
          Username = context?.LoginHint,
        };

        if (!local)
        {
          vm.ExternalProviders = new[] { new ExternalProvider { AuthenticationScheme = context.IdP } };
        }

        return vm;
      }

      var schemes = await _schemeProvider.GetAllSchemesAsync();

      var providers = schemes.Where(x => x.DisplayName != null || (x.Name.Equals(AccountOptions.WindowsAuthenticationSchemeName, StringComparison.OrdinalIgnoreCase))
          )
          .Select(x => new ExternalProvider
          {
            DisplayName = x.DisplayName,
            AuthenticationScheme = x.Name
          }).ToList();

      var allowLocal = true;
      if (context?.Client.ClientId != null)
      {
        var client = await _clientStore.FindEnabledClientByIdAsync(context.Client.ClientId);
        if (client != null)
        {
          allowLocal = client.EnableLocalLogin;

          if (client.IdentityProviderRestrictions != null && client.IdentityProviderRestrictions.Any())
          {
            providers = providers.Where(provider => client.IdentityProviderRestrictions.Contains(provider.AuthenticationScheme)).ToList();
          }
        }
      }

      return new LoginViewModel
      {
        AllowRememberLogin = AccountOptions.AllowRememberLogin,
        EnableLocalLogin = allowLocal && AccountOptions.AllowLocalLogin,
        ReturnUrl = returnUrl,
        Username = context?.LoginHint,
        ExternalProviders = providers.ToArray()
      };
    }

    private async Task<LoginViewModel> BuildLoginViewModelAsync(LoginInputModel model)
    {
      var vm = await BuildLoginViewModelAsync(model.ReturnUrl);
      vm.Username = model.Username;
      vm.RememberLogin = model.RememberLogin;
      return vm;
    }

    #endregion
    */
  }
}
