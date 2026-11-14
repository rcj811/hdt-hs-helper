// 測試環境沒有 WPF，補一個最小的 Point 讓 BotModel 能編譯
namespace System.Windows
{
	public struct Point
	{
		public double X, Y;
		public Point(double x, double y) { X = x; Y = y; }
	}
}
