using System;

namespace KrahmerSoft.ParallelFileCopierLib
{
	public class VerboseInfo
	{
		public int VerboseLevel { get; private set; }
		private string _message;
		private Func<string> _getMessage;
		public string Message
		{
			get
			{
				if (_getMessage != null)
				{
					_message = _getMessage();
					_getMessage = null;
				}

				return _message;
			}
		}

		public VerboseInfo(int verboseLevel, string message)
		{
			VerboseLevel = verboseLevel;
			_message = message;
		}

		public VerboseInfo(int verboseLevel, Func<string> getMessage)
		{
			VerboseLevel = verboseLevel;
			_getMessage = getMessage;
		}

	}
}