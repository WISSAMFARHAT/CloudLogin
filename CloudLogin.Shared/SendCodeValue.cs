﻿namespace AngryMonkey.Cloud.Login.DataContract;

public class SendCodeValue
{
	public SendCodeValue(string code, string address)
	{
		Code = code;
		Address = address;
	}

	public string Code { get; set; }
	public string Address { get; set; }
}
