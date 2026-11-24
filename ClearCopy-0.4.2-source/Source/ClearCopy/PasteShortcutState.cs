namespace ClearCopy;

public sealed class PasteShortcutState
{
	private bool leftControl;

	private bool rightControl;

	private bool leftShift;

	private bool rightShift;

	private bool leftAlt;

	private bool rightAlt;

	public bool Process(uint key, bool down, bool injected)
	{
		if (injected)
		{
			return false;
		}
		switch (key)
		{
		case 17u:
		case 162u:
			leftControl = down;
			return false;
		case 163u:
			rightControl = down;
			return false;
		case 16u:
		case 160u:
			leftShift = down;
			return false;
		case 161u:
			rightShift = down;
			return false;
		case 18u:
		case 164u:
			leftAlt = down;
			return false;
		case 165u:
			rightAlt = down;
			return false;
		default:
			if (key == 86 && down && (leftControl || rightControl) && !leftShift && !rightShift && !leftAlt)
			{
				return !rightAlt;
			}
			return false;
		}
	}
}
