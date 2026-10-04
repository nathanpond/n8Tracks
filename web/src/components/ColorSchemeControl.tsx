import { SegmentedControl, useMantineColorScheme, type MantineColorScheme } from '@mantine/core';

const options: { value: MantineColorScheme; label: string }[] = [
  { value: 'light', label: 'Light' },
  { value: 'dark', label: 'Dark' },
  { value: 'auto', label: 'Auto' },
];

function isColorScheme(value: string): value is MantineColorScheme {
  return options.some((option) => option.value === value);
}

/** Light, dark, or auto (follow the system). The provider remembers the choice in the browser. */
export function ColorSchemeControl() {
  const { colorScheme, setColorScheme } = useMantineColorScheme();

  return (
    <SegmentedControl
      size="xs"
      data={options}
      value={colorScheme}
      onChange={(value) => {
        if (isColorScheme(value)) {
          setColorScheme(value);
        }
      }}
    />
  );
}
