using Sandbox.Utility;
using System.Collections.Generic;

namespace SystemTests;

[TestClass]
[DoNotParallelize]
public class CommandLineTest
{
	[TestMethod]
	public void ParseLaunchArgumentsWithoutChangingStartupArguments()
	{
		var startupArguments = new Dictionary<string, string>( CommandLine.GetSwitches() );
		var startupCommandLine = CommandLine.Full;
		var arguments = CommandLine.Parse( "-RUNGAME \"facepunch.walker\" -webreferrer sbox.game" );

		Assert.AreEqual( "\"facepunch.walker\"", arguments["rungame"] );
		Assert.AreEqual( "sbox.game", arguments["webreferrer"] );
		Assert.AreEqual( startupCommandLine, CommandLine.Full );
		CollectionAssert.AreEquivalent( startupArguments.ToArray(), CommandLine.GetSwitches().ToArray() );

		var nextArguments = CommandLine.Parse( "-rungame facepunch.other" );
		Assert.AreEqual( "facepunch.other", nextArguments["rungame"] );
		Assert.IsFalse( nextArguments.ContainsKey( "webreferrer" ) );
		Assert.AreEqual( 0, CommandLine.Parse( "" ).Count );
	}

	/// <summary>
	/// The parser should split switches on +/- prefixes, keep quoted strings
	/// intact, and tolerate switch-name lookups with or without the prefix.
	/// CommandLine is process-global state, so we save and restore it around
	/// the test and don't run in parallel with anything else.
	/// </summary>
	[TestMethod]
	public void TestCommandLineParser()
	{
		var previousCommandLine = CommandLine.CommandLineString;

		try
		{
			CommandLine.CommandLineString = @"+game facepunch.walker garry.scenemap +something ""hello there im a string"" test -value 35455 +somethingelse ""a -switch inside a string""";
			CommandLine.Parse();

			Assert.AreEqual( "facepunch.walker garry.scenemap", CommandLine.GetSwitch( "game", string.Empty ) );
			Assert.AreEqual( @"""a -switch inside a string""", CommandLine.GetSwitch( "somethingelse", string.Empty ) );
			Assert.AreEqual( "35455", CommandLine.GetSwitch( "value", string.Empty ) );
			Assert.AreEqual( "35455", CommandLine.GetSwitch( "-value", string.Empty ) );
			Assert.AreEqual( @"""hello there im a string"" test", CommandLine.GetSwitch( "something", string.Empty ) );

			CommandLine.CommandLineString = @"D:\Facepunch\sbox\game\sbox-dev.dll -project D:\Facepunch\sbox\game\addons\sbox-deathmatch\sbdm.sbproj";
			CommandLine.Parse();

			Assert.AreEqual( @"D:\Facepunch\sbox\game\addons\sbox-deathmatch\sbdm.sbproj", CommandLine.GetSwitch( "project", string.Empty ) );
		}
		finally
		{
			CommandLine.CommandLineString = previousCommandLine;
			CommandLine.Parse();
		}
	}
}
