﻿using AngryMonkey.Cloud.Components;
using AngryMonkey.Cloud.Login.DataContract;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using System;
using System.Collections.Generic;
using System.Net.Mail;
using System.Text;
using Microsoft.Azure.Cosmos;
using System.Text.RegularExpressions;
using AngryMonkey.Cloud.Components.Icons;
using AngryMonkey.Cloud.Geography;
using System.Security.Cryptography;
using Newtonsoft.Json;

namespace AngryMonkey.Cloud.Login
{
	public partial class CloudLogin
	{
		public bool checkError { get; set; } = false;
		public bool IsLoading { get; set; } = false;
		protected string Title { get; set; } = string.Empty;
		protected string Subtitle { get; set; } = string.Empty;
		protected List<string> Errors { get; set; } = new List<string>();

		public Action OnInput { get; set; }
		public Guid UserId { get; set; } = Guid.NewGuid();

		private string _inputValue;
		Provider providerType { get; set; }

		[Parameter]
		public string InputValue
		{
			get => _inputValue;
			set
			{
				if (value == _inputValue)
					return;

				_inputValue = value;

				OnInput.Invoke();
			}
		}

		[Parameter] public string Logo { get; set; }
		public string FirstName { get; set; }
		public string LastName { get; set; }
		public string DisplayName { get; set; }
		public string VerificationValue { get; set; }
		public bool KeepMeSignedIn { get; set; }
		public bool WrongCode { get; set; } = false;
		public bool EmptyInput { get; set; } = false;
		public bool ExpiredCode { get; set; } = false;
		internal CosmosMethods Cosmos { get; set; }
		List<Provider> Providers { get; set; } = new();
		public bool DisplayInputValue { get; set; } = false;

		public string? VerificationCode { get; set; }
		public DateTimeOffset? VerificationCodeExpiry { get; set; }

		private VerificationCodeResult GetVerificationCodeResult(string code)
		{
			if (VerificationCode != code.Trim())
				return VerificationCodeResult.NotValid;

			if (VerificationCodeExpiry.HasValue && DateTimeOffset.UtcNow >= VerificationCodeExpiry.Value)
				return VerificationCodeResult.Expired;

			return VerificationCodeResult.Valid;
		}

		private enum VerificationCodeResult
		{
			Valid,
			NotValid,
			Expired
		}

		protected InputFormat InputValueFormat
		{
			get
			{
				if (string.IsNullOrEmpty(InputValue))
					return InputFormat.Other;

				if (IsValidEmail)
					return InputFormat.EmailAddress;

				if (cloudGeography.PhoneNumbers.IsValidPhoneNumber(InputValue))
					return InputFormat.PhoneNumber;

				return InputFormat.Other;
			}
		}

		protected ProcessState State { get; set; }
		private void SwitchState(ProcessState state)
		{
			State = state;

			switch (State)
			{
				case ProcessState.PendingSignIn:
					Title = "Sign in";
					Subtitle = string.Empty;
					DisplayInputValue = false;

					break;

				case ProcessState.PendingProviders:
					Title = "Register";
					Subtitle = "Choose a provider to register.";
					DisplayInputValue = true;

					break;

				case ProcessState.PendingAuthorization:
					Title = "Continue signing in";
					Subtitle = "Choose a provider to sign in.";
					DisplayInputValue = true;
					break;

				default:
					Title = "Untitled!!!";
					Subtitle = string.Empty;
					DisplayInputValue = false;
					break;
			}

			IsLoading = false;
			StateHasChanged();
		}

		protected enum ProcessState
		{
			PendingSignIn,
			PendingAuthorization,
			PendingProviders,
			PendingVerification,
			PendingRegisteration
		}

		protected override async Task OnAfterRenderAsync(bool firstRender)
		{
			if (firstRender)
			{
				Cosmos = new CosmosMethods(cloudLogin.Options.Cosmos.ConnectionString, cloudLogin.Options.Cosmos.DatabaseId, cloudLogin.Options.Cosmos.ContainerId);
				OnInput = () =>
				{
					//CheckFormat();
					StateHasChanged();
				};

				SwitchState(ProcessState.PendingSignIn);
			}
		}

		private async Task OnNextClicked(MouseEventArgs e)
		{
			if (string.IsNullOrEmpty(InputValue))
				return;

			IsLoading = true;

			InputValue = InputValue.ToLower();
			CloudUser? user;

			if (InputValueFormat == InputFormat.PhoneNumber)
			{
				InputValue = cloudGeography.PhoneNumbers.Get(InputValue).GetFullPhoneNumber();

				user = await Cosmos.GetUserByPhoneNumber(InputValue);
			}
			else
				user = await Cosmos.GetUserByEmailAddress(InputValue);

			CheckUser(user);
		}

		private void CheckUser(CloudUser? user)
		{
			if (user != null) // Existing user
			{
				Providers = user.Providers.Select(key => new Provider(key)).ToList();
				UserId = user.ID;

				AppendEmailCodeProvider();

				SwitchState(ProcessState.PendingAuthorization);
			}
			else // New user
			{
				Providers = cloudLogin.Options.Providers
					.Select(key => new Provider(key.Code))
					.Where(key => (key.ProcessEmailAddresses && InputValueFormat == InputFormat.EmailAddress)
								|| (key.ProcessPhoneNumbers && InputValueFormat == InputFormat.PhoneNumber))
					.ToList();

				AppendEmailCodeProvider();

				SwitchState(ProcessState.PendingProviders);
			}
		}

		private void AppendEmailCodeProvider()
		{
			if (cloudLogin.Options.AllowLoginWithEmailCode && InputValueFormat == InputFormat.EmailAddress)
				Providers.Add(new Provider("EmailAddress"));
		}

		private async Task OnContinueClicked(MouseEventArgs e)
		{
			IsLoading = true;
			CloudUser? user = await Cosmos.GetUserByPhoneNumber(InputValue);
			CheckUser(user);
		}

		private async Task OnBackClicked(MouseEventArgs e)
		{
			if (State == ProcessState.PendingSignIn)
				return;

			WrongCode = false;
			SwitchState(ProcessState.PendingSignIn);
		}

		private async Task RefreshVerificationCode()
		{
			VerificationCode = CreateRandomCode(6);
			VerificationCodeExpiry = DateTimeOffset.UtcNow.AddMinutes(5);

			ExpiredCode = false;
			WrongCode = false;

			if (InputValueFormat == InputFormat.EmailAddress)
				await SendEmail(InputValue, VerificationCode);
		}

		private async Task OnNewCodeClicked(MouseEventArgs e)
		{
			IsLoading = true;

			await RefreshVerificationCode();

			SwitchState(ProcessState.PendingVerification);
		}
		private async Task OnProviderClickedAsync(Provider provider)
		{
			providerType = provider;

			if (provider.Code.ToLower() == "emailaddress")
			{
				IsLoading = true;

				await RefreshVerificationCode();
				SwitchState(ProcessState.PendingVerification);
			}
			else ProviderSignInChallenge(provider.Code);
		}

		private string GetPhoneNumberWithoutCode(string phoneNumber)
		{
			Country? country = cloudGeography.Countries.GuessCountryByPhoneNumber(InputValue);

			if (country == null)
				return phoneNumber;

			return phoneNumber[$"+{country.CallingCode}".Length..];
		}

		private void ProviderSignInChallenge(string provider)
		{
			navigationManager.NavigateTo($"/cloudlogin/login/{provider}?input={InputValue}&redirectUri=/&keepMeSignedIn={KeepMeSignedIn}", true);
		}

		private void CustomSignInChallenge(CloudUser user)
		{
			Dictionary<string, object> userInfo = new()
			{
				{ "UserId", user.ID },
				{ "FirstName", user.FirstName },
				{ "LastName", user.LastName },
				{ "DisplayName", user.DisplayName },
				{ "Input", InputValue },
			};

			string userInfoJSON = JsonConvert.SerializeObject(userInfo);

			navigationManager.NavigateTo($"/cloudlogin/login/customlogin?userInfo={userInfoJSON}&keepMeSignedIn={KeepMeSignedIn}", true);
		}

		private async Task OnVerifyClicked(MouseEventArgs e)
		{
			if (providerType.Code.ToLower() == "emailaddress")
			{
				switch (GetVerificationCodeResult(VerificationValue))
				{
					case VerificationCodeResult.NotValid:
						WrongCode = true;
						return;

					case VerificationCodeResult.Expired:
						ExpiredCode = true;
						return;

					default: break;
				}

				IsLoading = true;

				CloudUser? checkUser = await Cosmos.GetUserByEmailAddress(InputValue);

				if (checkUser != null)
					CustomSignInChallenge(checkUser);

				SwitchState(ProcessState.PendingRegisteration);
			}
		}

		private async Task OnRegisterClicked(MouseEventArgs e)
		{
			//Register button is clicked => IsRegistered = true / IsVerified = true => Update user info => push to database => Login user
			if (string.IsNullOrEmpty(FirstName) || string.IsNullOrEmpty(FirstName) || string.IsNullOrEmpty(FirstName))//check if any of the input is empty
			{
				EmptyInput = true;
				StateHasChanged();
				return;
			}

			EmptyInput = false;
			IsLoading = true;

			CustomSignInChallenge(new CloudUser()
			{
				ID = UserId,
				FirstName = FirstName,
				LastName = LastName,
				DisplayName = DisplayName
			});
		}

		private static string CreateRandomCode(int length)
		{
			StringBuilder builder = new();

			for (int i = 0; i <= length; i++)
				builder.Append(new Random().Next(0, 9));

			return builder.ToString();
		}

		public class Provider
		{
			public Provider(string code)
			{
				Code = code;
			}

			public string Code { get; private set; }
			public string LowerCode => Code.ToLower();

			public string? Name => LowerCode switch
			{
				"microsoft" => "Microsoft",
				"google" => "Google",
				"emailaddress" => "Email Code",
				"phonenumber" => "SMS Code",
				_ => Code
			};

			public bool ProcessEmailAddresses => !Code.Equals("phonenumber", StringComparison.OrdinalIgnoreCase);
			public bool ProcessPhoneNumbers => !Code.Equals("emailaddress", StringComparison.OrdinalIgnoreCase);
		}

		//protected void CheckFormat()
		//{
		//    var checkEmail = IsValidEmail(Value);
		//    var checkPhoneNumber = IsValidPhoneNumber(Value);
		//    var CheckCharacters = HasCharacters(Value);

		//    if (checkEmail)
		//        InputType = InputFormat.Email;
		//    else if (checkPhoneNumber)
		//        if(!CheckCharacters)
		//            InputType = InputFormat.PhoneNumber;
		//    else if(CheckCharacters)
		//        InputType = InputFormat.Other;

		//    StateHasChanged();
		//}

		bool IsValidEmail => Regex.IsMatch(InputValue, @"\w+([-+.']\w+)*@\w+([-.]\w+)*\.\w+([-.]\w+)*");

		public async Task SendEmail(string receiver, string Code)
		{
			//string smtpEmail = "AngryMonkeyDev@gmail.com";
			//string smtpPassword = "nllvbaqoxvfqsssh";

			cloudLogin.Options.MailMessage.To.Add(receiver);
			cloudLogin.Options.MailMessage.Body = cloudLogin.Options.MailMessage.Body.Replace("{{code}}", Code);

			await cloudLogin.Options.SmtpClient.SendMailAsync(cloudLogin.Options.MailMessage);
		}
	}
}
