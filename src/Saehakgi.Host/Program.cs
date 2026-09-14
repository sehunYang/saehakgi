using Saehakgi.Core.Native;

// Launched by Chrome/Edge as a native-messaging host. Chrome passes the calling
// extension origin as an argument and communicates over stdin/stdout.
using var input = Console.OpenStandardInput();
using var output = Console.OpenStandardOutput();
NativeHost.RunLoop(input, output);
