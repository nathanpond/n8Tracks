import { Group, Title } from '@mantine/core';
import { useEffect, useRef, type ReactNode } from 'react';

/** A step's heading. It takes the focus when its step opens, so keyboard and screen-reader users land on it. */
export function StepHeading({ children }: { children: string }) {
  const heading = useRef<HTMLHeadingElement>(null);
  useEffect(() => {
    heading.current?.focus();
  }, []);

  return (
    <Title order={3} ref={heading} tabIndex={-1}>
      {children}
    </Title>
  );
}

export function StepActions({ children }: { children: ReactNode }) {
  return (
    <Group gap="sm" mt="md">
      {children}
    </Group>
  );
}
